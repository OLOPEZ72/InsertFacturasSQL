using System.Net.Http.Headers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public sealed record AiCallDiagnostic(
    string Stage,
    string Endpoint,
    int? HttpStatusCode,
    string? ErrorType,
    string? ErrorCode,
    string? SanitizedMessage,
    string Model,
    bool ApiKeyExists,
    bool FileSent,
    string MimeType,
    long? FileSizeBytes);

public sealed record AiExtractionResult(AiInvoiceExtraction? Extraction, string? Error, AiCallDiagnostic? Diagnostic = null);

public sealed class OpenAiInvoiceExtractor
{
    private const string Endpoint = "https://api.openai.com/v1/responses";
    private readonly HttpClient httpClient;

    public OpenAiInvoiceExtractor(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public AiExtractionResult Extract(string extractedText, string? apiKey, string? pdfPath = null)
    {
        FileInfo? fileInfo = !string.IsNullOrWhiteSpace(pdfPath) && File.Exists(pdfPath) ? new FileInfo(pdfPath) : null;
        bool fileSent = false;
        int? responseStatus = null;
        string model = ResolveModel(Environment.GetEnvironmentVariable("OPENAI_INVOICE_MODEL"));
        if (string.IsNullOrWhiteSpace(apiKey))
            return Failure("credential-check", null, null, null, "OPENAI_API_KEY no está configurada; se conserva el parser local.", model, false, false, fileInfo);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            object[] content = [new { type = "input_text", text = BuildPrompt(extractedText) }];
            if (fileInfo is not null)
            {
                string base64 = Convert.ToBase64String(File.ReadAllBytes(fileInfo.FullName));
                content = [
                    new { type = "input_text", text = BuildPrompt(extractedText) },
                    new { type = "input_file", filename = Path.GetFileName(pdfPath), file_data = $"data:application/pdf;base64,{base64}" }
                ];
                fileSent = true;
            }

            var payload = new
            {
                model,
                instructions = "Extrae datos de facturas y devuelve únicamente JSON válido.",
                text = new { format = BuildJsonSchemaFormat() },
                input = new[] { new { role = "user", content } }
            };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = httpClient.Send(request);
            responseStatus = (int)response.StatusCode;
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                (string? type, string? code, string? message) = ParseError(body);
                return Failure("http-response", (int)response.StatusCode, type, code, message ?? "OpenAI devolvió un error HTTP.", model, true, fileSent, fileInfo);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new FlexibleNullableDateTimeConverter());
            var extraction = JsonSerializer.Deserialize<AiInvoiceExtraction>(CleanJson(ReadOutputText(document)), options);
            return extraction is null
                ? Failure("response-parse", (int)response.StatusCode, "invalid_response", null, "La respuesta no contiene una extracción válida.", model, true, fileSent, fileInfo)
                : new AiExtractionResult(extraction, null, new AiCallDiagnostic("completed", Endpoint, (int)response.StatusCode, null, null, null, model, true, fileSent, "application/pdf", fileInfo?.Length));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            string stage = exception is TaskCanceledException ? "http-timeout" : exception is JsonException or KeyNotFoundException ? "response-parse" : "http-request";
            return Failure(stage, responseStatus, exception.GetType().Name, null, exception.Message, model, !string.IsNullOrWhiteSpace(apiKey), fileSent, fileInfo);
        }
    }

    public static string ResolveModel(string? configuredModel) =>
        string.IsNullOrWhiteSpace(configuredModel) ? "gpt-4o-mini" : configuredModel.Trim();

    private static AiExtractionResult Failure(string stage, int? status, string? type, string? code, string message, string model, bool keyExists, bool fileSent, FileInfo? fileInfo) =>
        new(null, message, new AiCallDiagnostic(stage, Endpoint, status, type, code, Sanitize(message), model, keyExists, fileSent, "application/pdf", fileInfo?.Length));

    private static object BuildJsonSchemaFormat() => new
    {
        type = "json_schema",
        name = "supplier_invoice_extraction",
        strict = true,
        schema = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                supplierName = new { type = new[] { "string", "null" } },
                supplierTaxId = new { type = new[] { "string", "null" } },
                customerName = new { type = new[] { "string", "null" } },
                customerTaxId = new { type = new[] { "string", "null" } },
                supplierEvidence = new { type = new[] { "string", "null" } },
                customerEvidence = new { type = new[] { "string", "null" } },
                invoiceNumber = new { type = new[] { "string", "null" } },
                invoiceDate = new { type = new[] { "string", "null" } },
                dueDate = new { type = new[] { "string", "null" } },
                currency = new { type = new[] { "string", "null" } },
                subtotal = new { type = new[] { "number", "null" } },
                taxAmount = new { type = new[] { "number", "null" } },
                total = new { type = new[] { "number", "null" } },
                confidence = new { type = "number", minimum = 0, maximum = 100 },
                warnings = new { type = "array", items = new { type = "string" } },
                items = new { type = "array", items = ItemSchema() },
                additionalCharges = new { type = "array", items = ChargeSchema() }
            },
            required = new[] { "supplierName", "supplierTaxId", "customerName", "customerTaxId", "supplierEvidence", "customerEvidence", "invoiceNumber", "invoiceDate", "dueDate", "currency", "subtotal", "taxAmount", "total", "confidence", "warnings", "items", "additionalCharges" }
        }
    };

    private static object PartySchema() => new
    {
        type = new[] { "object", "null" },
        additionalProperties = false,
        properties = new
        {
            name = new { type = new[] { "string", "null" } },
            taxId = new { type = new[] { "string", "null" } },
            evidenceInDocument = new { type = new[] { "boolean", "null" } }
        },
        required = new[] { "name", "taxId", "evidenceInDocument" }
    };

    private static object ItemSchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            description = new { type = new[] { "string", "null" } },
            quantity = new { type = new[] { "number", "null" } },
            unitPrice = new { type = new[] { "number", "null" } },
            unitPriceCalculated = new { type = "boolean" },
            baseAmount = new { type = new[] { "number", "null" } },
            taxRate = new { type = new[] { "number", "null" } },
            taxAmount = new { type = new[] { "number", "null" } },
            totalAmount = new { type = new[] { "number", "null" } }
        },
        required = new[] { "description", "quantity", "unitPrice", "unitPriceCalculated", "baseAmount", "taxRate", "taxAmount", "totalAmount" }
    };

    private static object ChargeSchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            description = new { type = new[] { "string", "null" } },
            baseAmount = new { type = new[] { "number", "null" } },
            taxRate = new { type = new[] { "number", "null" } },
            taxAmount = new { type = new[] { "number", "null" } },
            totalAmount = new { type = new[] { "number", "null" } }
        },
        required = new[] { "description", "baseAmount", "taxRate", "taxAmount", "totalAmount" }
    };

    private static (string? Type, string? Code, string? Message) ParseError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement error = document.RootElement.TryGetProperty("error", out JsonElement value) ? value : document.RootElement;
            return (
                error.TryGetProperty("type", out JsonElement type) ? type.GetString() : null,
                error.TryGetProperty("code", out JsonElement code) ? code.GetString() : null,
                error.TryGetProperty("message", out JsonElement message) ? message.GetString() : null);
        }
        catch (JsonException)
        {
            return ("http_error", null, "Respuesta de error no estructurada.");
        }
    }

    private static string Sanitize(string message)
    {
        string sanitized = message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return sanitized.Length <= 240 ? sanitized : sanitized[..240];
    }

    private sealed class FlexibleNullableDateTimeConverter : System.Text.Json.Serialization.JsonConverter<DateTime?>
    {
        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType != JsonTokenType.String) return null;
            string? value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value)) return null;
            string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-ddTHH:mm:ssZ"];
            return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed)
                ? parsed
                : DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed) ? parsed : null;
        }

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public static void MergeIntoDraft(SupplierInvoiceDraft draft, AiInvoiceExtraction extraction)
    {
        MarkLocalOrigins(draft);
        string? supplierName = extraction.SupplierName ?? extraction.Issuer?.Name;
        string? supplierTaxId = extraction.SupplierTaxId ?? extraction.Issuer?.TaxId;
        string? customerName = extraction.CustomerName ?? extraction.Customer?.Name;
        string? customerTaxId = extraction.CustomerTaxId ?? extraction.Customer?.TaxId;
        string? semanticError = ValidateSupplierCustomerRoles(extraction, supplierName, customerName, supplierTaxId, customerTaxId);
        bool issuerMatchesCustomer = !string.IsNullOrWhiteSpace(supplierName) &&
            ((!string.IsNullOrWhiteSpace(customerName) && string.Equals(supplierName.Trim(), customerName.Trim(), StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(supplierTaxId) && string.Equals(supplierTaxId.Trim(), customerTaxId?.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (semanticError is null && !string.IsNullOrWhiteSpace(supplierName) && !issuerMatchesCustomer)
        {
            Set(draft, "provider", supplierName.Trim(), value => draft.ProviderName = value);
            if (!string.IsNullOrWhiteSpace(supplierTaxId)) Set(draft, "supplierTaxId", supplierTaxId.Trim(), value => draft.SupplierTaxId = value);
        }
        else
        {
            draft.ProviderName = "";
            draft.SupplierTaxId = null;
            draft.FieldOrigins["provider"] = "AI";
            draft.FieldOrigins["supplierTaxId"] = "AI";
            draft.Warnings.Add("OpenAI no encontró evidencia suficiente de un proveedor; customer/billTo no se usa como supplier.");
            if (semanticError is not null) draft.Warnings.Add($"OpenAI error semántico: {semanticError}");
        }
        if (!string.IsNullOrWhiteSpace(extraction.InvoiceNumber)) Set(draft, "invoiceNumber", extraction.InvoiceNumber.Trim(), value => draft.InvoiceNumber = value);
        if (extraction.InvoiceDate.HasValue) Set(draft, "invoiceDate", extraction.InvoiceDate.Value, value => draft.InvoiceDate = value);
        if (extraction.DueDate.HasValue) Set(draft, "dueDate", extraction.DueDate.Value, value => draft.DueDate = value);
        string? currency = extraction.Currency ?? extraction.CurrencyCode;
        if (!string.IsNullOrWhiteSpace(currency)) Set(draft, "currency", currency.Trim().ToUpperInvariant(), value => draft.CurrencyCode = value);
        if (!string.IsNullOrWhiteSpace(extraction.MainDescription)) Set(draft, "description", extraction.MainDescription.Trim(), value => draft.MainDescription = value);
        if ((extraction.Subtotal ?? extraction.DocumentSubtotal).HasValue) Set(draft, "subtotal", (extraction.Subtotal ?? extraction.DocumentSubtotal)!.Value, value => draft.DocumentSubtotal = value);
        if ((extraction.TaxAmount ?? extraction.DocumentTaxTotal).HasValue) Set(draft, "tax", (extraction.TaxAmount ?? extraction.DocumentTaxTotal)!.Value, value => draft.DocumentTaxTotal = value);
        if ((extraction.Total ?? extraction.DocumentTotal).HasValue) Set(draft, "total", (extraction.Total ?? extraction.DocumentTotal)!.Value, value => draft.DocumentTotal = value);

        if (extraction.Items.Count > 0 && extraction.Items.All(IsValidAiItem))
        {
            draft.Items = extraction.Items.Select((item, index) => new SupplierInvoiceItemDraft
            {
                Position = index + 1,
                Description = item.Description!.Trim(),
                Amount = (item.Quantity ?? item.Amount)!.Value,
                UnitPrice = item.UnitPrice ?? (item.BaseAmount ?? item.DocumentLineNetAmount)!.Value / (item.Quantity ?? item.Amount)!.Value,
                UnitPriceCalculated = item.UnitPriceCalculated || !item.UnitPrice.HasValue,
                IVA = (item.TaxRate ?? item.IVA)!.Value,
                DocumentLineNetAmount = item.BaseAmount ?? item.DocumentLineNetAmount,
                DocumentLineTaxAmount = item.TaxAmount,
                DocumentLineTotal = item.TotalAmount ?? item.DocumentLineTotal
            }).ToList();
            draft.FieldOrigins["items"] = "AI";
        }
        else if (extraction.Items.Count > 0)
            draft.Warnings.Add("OpenAI devolvió items incompletos; se conserva el parser local para los items.");
        if (extraction.AdditionalCharges.Count > 0)
        {
            foreach (AiAdditionalChargeExtraction charge in extraction.AdditionalCharges)
            {
                if (string.IsNullOrWhiteSpace(charge.Description) || !charge.BaseAmount.HasValue || !charge.TaxRate.HasValue)
                {
                    draft.Warnings.Add("OpenAI devolvió un cargo adicional incompleto; no se añade.");
                    continue;
                }
                decimal tax = charge.TaxAmount ?? decimal.Round(charge.BaseAmount.Value * charge.TaxRate.Value / 100m, 2, MidpointRounding.AwayFromZero);
                decimal total = charge.TotalAmount ?? charge.BaseAmount.Value + tax;
                draft.Items.Add(new SupplierInvoiceItemDraft
                {
                    Position = draft.Items.Count + 1,
                    Description = charge.Description.Trim(),
                    Amount = 1m,
                    UnitPrice = charge.BaseAmount.Value,
                    UnitPriceCalculated = true,
                    IVA = charge.TaxRate.Value,
                    DocumentLineNetAmount = charge.BaseAmount.Value,
                    DocumentLineTaxAmount = tax,
                    DocumentLineTotal = total
                });
            }
            draft.FieldOrigins["additionalCharges"] = "AI";
            draft.FieldOrigins["items"] = "AI";
        }
        foreach (string warning in extraction.Warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)))
            draft.Warnings.Add($"OpenAI: {warning}");
    }

    private static string? ValidateSupplierCustomerRoles(AiInvoiceExtraction extraction, string? supplierName, string? customerName, string? supplierTaxId, string? customerTaxId)
    {
        if (string.IsNullOrWhiteSpace(supplierName)) return "supplierName es null o vacío.";
        if (string.Equals(supplierName.Trim(), customerName?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(supplierTaxId) && string.Equals(supplierTaxId.Trim(), customerTaxId?.Trim(), StringComparison.OrdinalIgnoreCase)))
            return "supplier y customer representan la misma entidad.";
        string evidence = extraction.SupplierEvidence ?? "";
        if (Regex.IsMatch(evidence, @"facturar\s+a|cliente|bill\s*to|customer|destinatario|receptor", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "supplierEvidence contiene etiquetas propias del customer.";
        if (string.IsNullOrWhiteSpace(evidence)) return "supplierEvidence no aporta evidencia de emisor.";
        return null;
    }

    private static bool IsValidAiItem(AiInvoiceItemExtraction item) =>
        !string.IsNullOrWhiteSpace(item.Description) && (item.Quantity ?? item.Amount) is > 0 && (item.UnitPrice is null || item.UnitPrice is >= 0) &&
        (item.TaxRate ?? item.IVA) is >= 0 and <= 100 && (item.BaseAmount ?? item.DocumentLineNetAmount).HasValue &&
        (item.TotalAmount ?? item.DocumentLineTotal).HasValue;

    private static void MarkLocalOrigins(SupplierInvoiceDraft draft)
    {
        if (!string.IsNullOrWhiteSpace(draft.ProviderName)) draft.FieldOrigins["provider"] = "LocalParser";
        if (!string.IsNullOrWhiteSpace(draft.SupplierTaxId)) draft.FieldOrigins["supplierTaxId"] = "LocalParser";
        if (!string.IsNullOrWhiteSpace(draft.InvoiceNumber)) draft.FieldOrigins["invoiceNumber"] = "LocalParser";
        if (draft.InvoiceDate != DateTime.MinValue) draft.FieldOrigins["invoiceDate"] = "LocalParser";
        if (draft.CurrencyCode is not null) draft.FieldOrigins["currency"] = "LocalParser";
        if (draft.Items.Count > 0) draft.FieldOrigins["items"] = "LocalParser";
    }

    private static void Set<T>(SupplierInvoiceDraft draft, string field, T value, Action<T> setter)
    {
        setter(value);
        draft.FieldOrigins[field] = "AI";
    }

    private static string BuildPrompt(string text) =>
        "Eres un experto en interpretación de facturas de proveedores europeas. Extrae con máxima precisión del PDF completo: texto, tablas, logotipos, estructura visual, encabezados y pies. " +
        "Identifica siempre supplier (issuer) y customer (bill to) como entidades distintas: supplier es exclusivamente la empresa que EMITE y customer es exclusivamente la empresa que RECIBE o es facturada. Las etiquetas Facturar a, Cliente, Bill to, Customer, Destinatario y Receptor siempre identifican customer y nunca supplier. Si IBYS TECHNOLOGIES aparece en ese bloque, debe ir a customer. Usa logotipo, cabecera, razón social, dirección fiscal y contexto visual para identificar supplier; nunca infieras supplier desde el bloque de facturación. Si hay duda, supplierName y supplierTaxId deben ser null. No inventes datos. " +
        "Devuelve supplierEvidence y customerEvidence describiendo las etiquetas y estructura visual que justifican cada rol. Para cargos no pertenecientes a una línea de producto devuelve additionalCharges con description, baseAmount, taxRate, taxAmount y totalAmount. " +
        "Respeta literalmente subtotal/base imponible, taxAmount/cuota IVA y total final; no recalcules importes impresos. Para cada item devuelve description, quantity, unitPrice, unitPriceCalculated, taxRate, baseAmount, taxAmount y totalAmount. Si unitPrice no aparece explícitamente, usa null; solo puedes calcularlo desde baseAmount/quantity y marcar unitPriceCalculated=true. Devuelve fechas ISO YYYY-MM-DD, moneda EUR/USD/GBP o null, confidence entre 0 y 100 y warnings para cualquier duda. Devuelve exclusivamente el JSON solicitado. Texto PdfPig de apoyo:\n" + text;

    private static string? ReadOutputText(JsonDocument document)
    {
        if (document.RootElement.TryGetProperty("output_text", out JsonElement outputText)) return outputText.GetString();
        if (document.RootElement.TryGetProperty("choices", out JsonElement choices))
            return choices[0].GetProperty("message").GetProperty("content").GetString();
        if (document.RootElement.TryGetProperty("output", out JsonElement output))
            foreach (JsonElement item in output.EnumerateArray())
                if (item.TryGetProperty("content", out JsonElement parts))
                    foreach (JsonElement part in parts.EnumerateArray())
                        if (part.TryGetProperty("text", out JsonElement text)) return text.GetString();
        return null;
    }

    private static string CleanJson(string? value)
    {
        string text = value?.Trim() ?? "{}";
        return text.StartsWith("```") ? text.Trim('`', ' ', '\r', '\n').Replace("json\n", "", StringComparison.OrdinalIgnoreCase) : text;
    }
}

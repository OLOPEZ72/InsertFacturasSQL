using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public sealed record AiExtractionResult(AiInvoiceExtraction? Extraction, string? Error);

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
        if (string.IsNullOrWhiteSpace(apiKey))
            return new AiExtractionResult(null, "OPENAI_API_KEY no está configurada; se conserva el parser local.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            object[] content = [new { type = "input_text", text = BuildPrompt(extractedText) }];
            if (!string.IsNullOrWhiteSpace(pdfPath) && File.Exists(pdfPath))
            {
                string base64 = Convert.ToBase64String(File.ReadAllBytes(pdfPath));
                content = [
                    new { type = "input_text", text = BuildPrompt(extractedText) },
                    new { type = "input_file", filename = Path.GetFileName(pdfPath), file_data = $"data:application/pdf;base64,{base64}" }
                ];
            }

            var payload = new
            {
                model = "gpt-4o-mini",
                instructions = "Extrae datos de facturas y devuelve únicamente JSON válido.",
                input = new[] { new { role = "user", content } }
            };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = httpClient.Send(request);
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return new AiExtractionResult(null, $"OpenAI no pudo extraer datos (HTTP {(int)response.StatusCode}).");

            using JsonDocument document = JsonDocument.Parse(body);
            var extraction = JsonSerializer.Deserialize<AiInvoiceExtraction>(CleanJson(ReadOutputText(document)), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return new AiExtractionResult(extraction, extraction is null ? "OpenAI devolvió una respuesta vacía." : null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return new AiExtractionResult(null, "No se pudo completar la extracción opcional con OpenAI.");
        }
    }

    public static void MergeIntoDraft(SupplierInvoiceDraft draft, AiInvoiceExtraction extraction)
    {
        MarkLocalOrigins(draft);
        bool issuerMatchesCustomer = extraction.Issuer is not null && extraction.Customer is not null &&
            ((!string.IsNullOrWhiteSpace(extraction.Issuer.Name) && string.Equals(extraction.Issuer.Name.Trim(), extraction.Customer.Name?.Trim(), StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(extraction.Issuer.TaxId) && string.Equals(extraction.Issuer.TaxId.Trim(), extraction.Customer.TaxId?.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (extraction.Issuer is not null && !issuerMatchesCustomer)
        {
            if (!string.IsNullOrWhiteSpace(extraction.Issuer.Name)) Set(draft, "provider", extraction.Issuer.Name.Trim(), value => draft.ProviderName = value);
            if (!string.IsNullOrWhiteSpace(extraction.Issuer.TaxId)) Set(draft, "supplierTaxId", extraction.Issuer.TaxId.Trim(), value => draft.SupplierTaxId = value);
        }
        else
        {
            draft.ProviderName = "";
            draft.SupplierTaxId = null;
            draft.FieldOrigins["provider"] = "AI";
            draft.FieldOrigins["supplierTaxId"] = "AI";
            draft.Warnings.Add("OpenAI no encontró evidencia suficiente de un proveedor; customer/billTo no se usa como supplier.");
        }
        if (!string.IsNullOrWhiteSpace(extraction.InvoiceNumber)) Set(draft, "invoiceNumber", extraction.InvoiceNumber.Trim(), value => draft.InvoiceNumber = value);
        if (extraction.InvoiceDate.HasValue) Set(draft, "invoiceDate", extraction.InvoiceDate.Value, value => draft.InvoiceDate = value);
        if (extraction.DueDate.HasValue) Set(draft, "dueDate", extraction.DueDate.Value, value => draft.DueDate = value);
        if (!string.IsNullOrWhiteSpace(extraction.CurrencyCode)) Set(draft, "currency", extraction.CurrencyCode.Trim().ToUpperInvariant(), value => draft.CurrencyCode = value);
        if (!string.IsNullOrWhiteSpace(extraction.MainDescription)) Set(draft, "description", extraction.MainDescription.Trim(), value => draft.MainDescription = value);
        if (extraction.DocumentSubtotal.HasValue) Set(draft, "subtotal", extraction.DocumentSubtotal.Value, value => draft.DocumentSubtotal = value);
        if (extraction.DocumentTaxTotal.HasValue) Set(draft, "tax", extraction.DocumentTaxTotal.Value, value => draft.DocumentTaxTotal = value);
        if (extraction.DocumentTotal.HasValue) Set(draft, "total", extraction.DocumentTotal.Value, value => draft.DocumentTotal = value);

        if (extraction.Items.Count > 0 && extraction.Items.All(IsValidAiItem))
        {
            draft.Items = extraction.Items.Select((item, index) => new SupplierInvoiceItemDraft
            {
                Position = index + 1,
                Description = item.Description!.Trim(),
                Amount = (item.Quantity ?? item.Amount)!.Value,
                UnitPrice = item.UnitPrice ?? (item.BaseAmount ?? item.DocumentLineNetAmount)!.Value / (item.Quantity ?? item.Amount)!.Value,
                UnitPriceCalculated = !item.UnitPrice.HasValue,
                IVA = (item.TaxRate ?? item.IVA)!.Value,
                DocumentLineNetAmount = item.BaseAmount ?? item.DocumentLineNetAmount,
                DocumentLineTaxAmount = item.TaxAmount,
                DocumentLineTotal = item.TotalAmount ?? item.DocumentLineTotal
            }).ToList();
            draft.FieldOrigins["items"] = "AI";
        }
        else if (extraction.Items.Count > 0)
            draft.Warnings.Add("OpenAI devolvió items incompletos; se conserva el parser local para los items.");
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
        "Devuelve JSON con issuer { name, taxId, evidenceInDocument }, customer { name, taxId }, " +
        "invoiceNumber, invoiceDate, dueDate, currencyCode, documentSubtotal, documentTaxTotal, " +
        "documentTotal e items[]. Cada item debe tener description, quantity, unitPrice (solo si está explícito), " +
        "baseAmount, taxRate, taxAmount y totalAmount. issuer es el proveedor y customer/billTo es siempre el " +
        "receptor: nunca uses customer como issuer. Si el proveedor solo aparece en logo/imagen o no hay evidencia " +
        "suficiente, issuer=null. Copia literalmente los importes impresos y no sustituyas baseAmount/totalAmount " +
        "por quantity*unitPrice. Texto PdfPig de apoyo:\n" + text;

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

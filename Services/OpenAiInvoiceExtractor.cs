using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public sealed record AiExtractionResult(AiInvoiceExtraction? Extraction, string? Error);

public sealed class OpenAiInvoiceExtractor
{
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";
    private readonly HttpClient httpClient;

    public OpenAiInvoiceExtractor(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public AiExtractionResult Extract(string extractedText, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new AiExtractionResult(null, "OPENAI_API_KEY no está configurada; se conserva el parser local.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var payload = new
            {
                model = "gpt-4o-mini",
                temperature = 0,
                response_format = new { type = "json_object" },
                messages = new[]
                {
                    new { role = "system", content = "Extrae datos de facturas. Devuelve únicamente JSON válido, sin inventar valores. Usa null si un dato no aparece." },
                    new { role = "user", content = BuildPrompt(extractedText) }
                }
            };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = httpClient.Send(request);
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return new AiExtractionResult(null, $"OpenAI no pudo extraer datos (HTTP {(int)response.StatusCode}).");

            using JsonDocument document = JsonDocument.Parse(body);
            string? content = document.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString();
            var extraction = JsonSerializer.Deserialize<AiInvoiceExtraction>(content ?? "{}", new JsonSerializerOptions
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

        if (!string.IsNullOrWhiteSpace(extraction.ProviderName)) Set(draft, "provider", extraction.ProviderName.Trim(), value => draft.ProviderName = value);
        if (!string.IsNullOrWhiteSpace(extraction.SupplierTaxId)) Set(draft, "supplierTaxId", extraction.SupplierTaxId.Trim(), value => draft.SupplierTaxId = value);
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
                Description = item.Description?.Trim() ?? "",
                Amount = item.Amount!.Value,
                UnitPrice = item.UnitPrice!.Value,
                IVA = item.IVA!.Value,
                DocumentLineNetAmount = item.DocumentLineNetAmount,
                DocumentLineTotal = item.DocumentLineTotal
            }).ToList();
            draft.FieldOrigins["items"] = "AI";
        }
        else if (extraction.Items.Count > 0)
            draft.Warnings.Add("OpenAI devolvió items incompletos o sin confianza suficiente; se conserva el parser local para los items.");
    }

    private static bool IsValidAiItem(AiInvoiceItemExtraction item) =>
        !string.IsNullOrWhiteSpace(item.Description) && item.Amount is > 0 && item.UnitPrice is >= 0 && item.IVA is >= 0 and <= 100 &&
        item.DocumentLineNetAmount.HasValue && item.DocumentLineTotal.HasValue;

    private static void MarkLocalOrigins(SupplierInvoiceDraft draft)
    {
        if (!string.IsNullOrWhiteSpace(draft.ProviderName)) draft.FieldOrigins["provider"] = "LocalParser";
        if (!string.IsNullOrWhiteSpace(draft.SupplierTaxId)) draft.FieldOrigins["supplierTaxId"] = "LocalParser";
        if (!string.IsNullOrWhiteSpace(draft.InvoiceNumber)) draft.FieldOrigins["invoiceNumber"] = "LocalParser";
        if (draft.InvoiceDate != DateTime.MinValue) draft.FieldOrigins["invoiceDate"] = "LocalParser";
        if (draft.DueDate.HasValue) draft.FieldOrigins["dueDate"] = "LocalParser";
        if (!string.IsNullOrWhiteSpace(draft.CurrencyCode)) draft.FieldOrigins["currency"] = "LocalParser";
        if (!string.IsNullOrWhiteSpace(draft.MainDescription)) draft.FieldOrigins["description"] = "LocalParser";
        if (draft.DocumentSubtotal.HasValue) draft.FieldOrigins["subtotal"] = "LocalParser";
        if (draft.DocumentTaxTotal.HasValue) draft.FieldOrigins["tax"] = "LocalParser";
        if (draft.DocumentTotal.HasValue) draft.FieldOrigins["total"] = "LocalParser";
        if (draft.Items.Count > 0) draft.FieldOrigins["items"] = "LocalParser";
    }

    private static void Set<T>(SupplierInvoiceDraft draft, string field, T value, Action<T> setter)
    {
        setter(value);
        draft.FieldOrigins[field] = "AI";
    }

    private static string BuildPrompt(string text) => $"""
Extrae estos campos: providerName, supplierTaxId, invoiceNumber, invoiceDate (ISO yyyy-MM-dd), dueDate (ISO yyyy-MM-dd), currencyCode, mainDescription, documentSubtotal, documentTaxTotal, documentTotal e items[]. Cada item debe tener description, amount, unitPrice, iva, documentLineNetAmount y documentLineTotal. No confundas cliente/receptor con proveedor y no adivines empresas. Texto:
{text}
""";
}

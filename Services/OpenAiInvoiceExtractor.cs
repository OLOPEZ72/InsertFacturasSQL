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
        if (string.IsNullOrWhiteSpace(draft.ProviderName)) draft.ProviderName = extraction.ProviderName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(draft.SupplierTaxId)) draft.SupplierTaxId = extraction.SupplierTaxId?.Trim();
        if (string.IsNullOrWhiteSpace(draft.InvoiceNumber)) draft.InvoiceNumber = extraction.InvoiceNumber?.Trim() ?? "";
        if (draft.InvoiceDate == DateTime.MinValue && extraction.InvoiceDate.HasValue) draft.InvoiceDate = extraction.InvoiceDate.Value;
        if (!draft.DueDate.HasValue) draft.DueDate = extraction.DueDate;
        if (string.IsNullOrWhiteSpace(draft.CurrencyCode)) draft.CurrencyCode = extraction.CurrencyCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(draft.MainDescription)) draft.MainDescription = extraction.MainDescription?.Trim() ?? "";
        draft.DocumentSubtotal ??= extraction.DocumentSubtotal;
        draft.DocumentTaxTotal ??= extraction.DocumentTaxTotal;
        draft.DocumentTotal ??= extraction.DocumentTotal;
        if (draft.Items.Count == 0 && extraction.Items.Count > 0)
        {
            draft.Items = extraction.Items.Select((item, index) => new SupplierInvoiceItemDraft
            {
                Position = index + 1,
                Description = item.Description?.Trim() ?? "",
                Amount = item.Amount,
                UnitPrice = item.UnitPrice,
                IVA = item.IVA,
                DocumentLineNetAmount = item.DocumentLineNetAmount,
                DocumentLineTotal = item.DocumentLineTotal
            }).ToList();
        }
    }

    private static string BuildPrompt(string text) => $"""
Extrae estos campos: providerName, supplierTaxId, invoiceNumber, invoiceDate (ISO yyyy-MM-dd), dueDate (ISO yyyy-MM-dd), currencyCode, mainDescription, documentSubtotal, documentTaxTotal, documentTotal e items[]. Cada item debe tener description, amount, unitPrice, iva, documentLineNetAmount y documentLineTotal. No confundas cliente/receptor con proveedor y no adivines empresas. Texto:
{text}
""";
}

using InsertFacturasSQL.Models;
using InsertFacturasSQL.Services;

namespace InsertFacturasSQL.Tests;

public sealed class OpenAiInvoiceExtractorTests
{
    [Fact]
    public void Extract_WithoutKey_DoesNotCallNetworkOrExposeCredentials()
    {
        var extractor = new OpenAiInvoiceExtractor(new HttpClient(new ThrowingHandler()));

        AiExtractionResult result = extractor.Extract("texto de prueba", null);

        Assert.Null(result.Extraction);
        Assert.Contains("OPENAI_API_KEY", result.Error);
        Assert.False(result.Diagnostic?.ApiKeyExists);
    }

    [Fact]
    public void Extract_UsesConfiguredInvoiceModelWhenProvided()
    {
        Assert.Equal("test-invoice-model", OpenAiInvoiceExtractor.ResolveModel(" test-invoice-model "));
        Assert.Equal("gpt-4o-mini", OpenAiInvoiceExtractor.ResolveModel(null));
    }

    [Fact]
    public void Extract_OnHttpError_ReturnsSafeDiagnosticWithoutResponseBody()
    {
        const string body = "{\"error\":{\"type\":\"invalid_request_error\",\"code\":\"invalid_api_key\",\"message\":\"bad key\"}}";
        var extractor = new OpenAiInvoiceExtractor(new HttpClient(new ErrorHandler(body)));

        AiExtractionResult result = extractor.Extract("texto", "secret-key");

        Assert.Equal("http-response", result.Diagnostic?.Stage);
        Assert.Equal(401, result.Diagnostic?.HttpStatusCode);
        Assert.Equal("invalid_request_error", result.Diagnostic?.ErrorType);
        Assert.Equal("invalid_api_key", result.Diagnostic?.ErrorCode);
        Assert.DoesNotContain("secret-key", result.Diagnostic?.SanitizedMessage ?? "");
    }

    [Fact]
    public void MergeIntoDraft_PrioritizesValidAiValues()
    {
        var draft = new SupplierInvoiceDraft
        {
            InvoiceNumber = "LOCAL-1",
            MainDescription = "Local"
        };
        var extraction = new AiInvoiceExtraction
        {
            InvoiceNumber = "AI-2",
            Issuer = new AiPartyExtraction { Name = "Proveedor AI", EvidenceInDocument = true },
            SupplierEvidence = "Cabecera superior y razón social del emisor",
            ProviderName = "Proveedor AI",
            MainDescription = "AI",
            Items = [new AiInvoiceItemExtraction { Description = "Servicio", Amount = 1, UnitPrice = 10, IVA = 21, DocumentLineNetAmount = 10, DocumentLineTotal = 12.1m }]
        };

        OpenAiInvoiceExtractor.MergeIntoDraft(draft, extraction);

        Assert.Equal("AI-2", draft.InvoiceNumber);
        Assert.Equal("AI", draft.MainDescription);
        Assert.Equal("Proveedor AI", draft.ProviderName);
        Assert.Single(draft.Items);
        Assert.Equal("AI", draft.FieldOrigins["invoiceNumber"]);
        Assert.Equal("AI", draft.FieldOrigins["items"]);
    }

    [Fact]
    public void Extract_ParsesStructuredResponse()
    {
        const string body = "{\"choices\":[{\"message\":{\"content\":\"{\\\"invoiceNumber\\\":\\\"AI-1\\\",\\\"currencyCode\\\":\\\"EUR\\\"}\"}}]}";
        var extractor = new OpenAiInvoiceExtractor(new HttpClient(new StaticHandler(body)));

        AiExtractionResult result = extractor.Extract("texto", "test-key");

        Assert.Null(result.Error);
        Assert.Equal("AI-1", result.Extraction?.InvoiceNumber);
        Assert.Equal("EUR", result.Extraction?.CurrencyCode);
    }

    [Fact]
    public void Extract_IncludesOriginalPdfWhenPathIsProvided()
    {
        string path = Path.Combine(Path.GetTempPath(), $"invoice-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, [1, 2, 3]);
        var handler = new CapturingHandler("{\"output_text\":\"{}\"}");
        try
        {
            _ = new OpenAiInvoiceExtractor(new HttpClient(handler)).Extract("texto", "test-key", path);
            Assert.Contains("input_file", handler.RequestBody);
            Assert.Contains("data:application/pdf;base64", handler.RequestBody);
            Assert.Contains("json_schema", handler.RequestBody);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MergeIntoDraft_UsesLocalParserWhenAiFieldIsNull()
    {
        var draft = new SupplierInvoiceDraft { InvoiceNumber = "LOCAL-1" };
        OpenAiInvoiceExtractor.MergeIntoDraft(draft, new AiInvoiceExtraction());

        Assert.Equal("LOCAL-1", draft.InvoiceNumber);
        Assert.Equal("LocalParser", draft.FieldOrigins["invoiceNumber"]);
    }

    [Fact]
    public void MergeIntoDraft_NeverUsesCustomerAsSupplierAndCalculatesOnlyMissingUnitPrice()
    {
        var draft = new SupplierInvoiceDraft { ProviderName = "Cliente local" };
        var extraction = new AiInvoiceExtraction
        {
            Issuer = new AiPartyExtraction { Name = "Cliente local", TaxId = "ES123" },
            Customer = new AiPartyExtraction { Name = "Cliente local", TaxId = "ES123" },
            Items = [new AiInvoiceItemExtraction
            {
                Description = "Producto",
                Quantity = 2,
                BaseAmount = 20,
                TaxRate = 21,
                TaxAmount = 4.2m,
                TotalAmount = 24.2m
            }]
        };

        OpenAiInvoiceExtractor.MergeIntoDraft(draft, extraction);

        Assert.Empty(draft.ProviderName);
        SupplierInvoiceItemDraft item = Assert.Single(draft.Items);
        Assert.Equal(10m, item.UnitPrice);
        Assert.True(item.UnitPriceCalculated);
        Assert.Equal(20m, item.DocumentLineNetAmount);
        Assert.Equal(24.2m, item.DocumentLineTotal);
    }

    [Fact]
    public void MergeIntoDraft_UsesPrintedAmountsAndAddsAdditionalCharges()
    {
        var draft = new SupplierInvoiceDraft();
        OpenAiInvoiceExtractor.MergeIntoDraft(draft, new AiInvoiceExtraction
        {
            SupplierName = "Carrefour",
            SupplierEvidence = "Cabecera superior",
            Items = [new AiInvoiceItemExtraction { Description = "Leche", Quantity = 60, BaseAmount = 50.77m, TaxRate = 4, TaxAmount = 2.03m, TotalAmount = 52.80m }],
            AdditionalCharges = [new AiAdditionalChargeExtraction { Description = "GASTOS DE ENVÍO", BaseAmount = 3.30m, TaxRate = 21, TaxAmount = 0.69m, TotalAmount = 3.99m }]
        });

        Assert.Equal(2, draft.Items.Count);
        Assert.Equal(50.77m, draft.Items[0].CalculatedNetAmount);
        Assert.Equal(2.03m, draft.Items[0].CalculatedTaxAmount);
        Assert.Equal(52.80m, draft.Items[0].CalculatedTotal);
        Assert.Equal(3.30m, draft.Items[1].CalculatedNetAmount);
        Assert.Equal(0.69m, draft.Items[1].CalculatedTaxAmount);
        Assert.Equal(3.99m, draft.Items[1].CalculatedTotal);
        Assert.Equal(54.07m, draft.CalculatedSubtotal);
        Assert.Equal(2.72m, draft.CalculatedTaxTotal);
        Assert.Equal(56.79m, draft.CalculatedTotal);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("network must not be called");
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
    }

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class ErrorHandler(string body) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent(body) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}

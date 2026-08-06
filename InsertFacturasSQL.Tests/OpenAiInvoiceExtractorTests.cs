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
    }

    [Fact]
    public void MergeIntoDraft_OnlyFillsMissingLocalValues()
    {
        var draft = new SupplierInvoiceDraft
        {
            InvoiceNumber = "LOCAL-1",
            MainDescription = "Local"
        };
        var extraction = new AiInvoiceExtraction
        {
            InvoiceNumber = "AI-2",
            ProviderName = "Proveedor AI",
            MainDescription = "AI",
            Items = [new AiInvoiceItemExtraction { Description = "Servicio", Amount = 1, UnitPrice = 10, IVA = 21 }]
        };

        OpenAiInvoiceExtractor.MergeIntoDraft(draft, extraction);

        Assert.Equal("LOCAL-1", draft.InvoiceNumber);
        Assert.Equal("Local", draft.MainDescription);
        Assert.Equal("Proveedor AI", draft.ProviderName);
        Assert.Single(draft.Items);
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
}

using InsertFacturasSQL.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace InsertFacturasSQL.Tests;

public sealed class SupplierInvoicePdfFunctionalTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"InsertFacturasSQL.Functional-{Guid.NewGuid():N}");

    public SupplierInvoicePdfFunctionalTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void SyntheticPdf_CanBeReadParsedAndPreparedForPreview()
    {
        string path = Path.Combine(_directory, "factura-sintetica.pdf");
        CreateSyntheticPdf(path,
        [
            "FACTURA",
            "Example AI OpCo, LLC",
            "EU OSS VAT: EU123456789",
            "Numero de factura: SYNTH-ABC-0031",
            "Fecha de emision: 1 de agosto de 2026",
            "Fecha de vencimiento: 1 de agosto de 2026",
            "Facturar a",
            "Example Client SA",
            "Moneda: US$",
            "Descripcion: Business Subscription per seat",
            "Periodo: 1 ago 2026 - 1 sept 2026",
            "Cantidad: 5",
            "Precio unitario: 25,00 US$",
            "IVA: 21 %",
            "Importe base: 125,00 US$",
            "Subtotal: 125,00 US$",
            "IVA total: 26,25 US$",
            "Total: 151,25 US$",
            "Importe adeudado: 151,25 US$"
        ]);

        var document = new PdfDocumentReader().Read(path);
        var draft = new SupplierInvoiceDraftBuilder().Build(document, 321);

        Assert.True(document.IsSuccess);
        Assert.Equal("Example AI OpCo, LLC", draft.ProviderName);
        Assert.Equal("EU123456789", draft.SupplierTaxId);
        Assert.Equal("SYNTH-ABC-0031", draft.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 8, 1), draft.InvoiceDate);
        Assert.Equal("USD", draft.CurrencyCode);
        Assert.Single(draft.Items);
        Assert.Equal(151.25m, draft.CalculatedTotal);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static void CreateSyntheticPdf(string path, IReadOnlyList<string> lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        int y = 800;

        foreach (string line in lines)
        {
            page.AddText(line, 10, new PdfPoint(30, y), font);
            y -= 24;
        }

        File.WriteAllBytes(path, builder.Build());
    }
}

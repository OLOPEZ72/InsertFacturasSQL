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
            "Proveedor: Proveedor de prueba",
            "CIF: B12345678",
            "Factura: SYNTH-2026-001",
            "Fecha: 06/08/2026",
            "Descripcion: Servicio sintetico",
            "Moneda: EUR",
            "Subtotal: 100.00",
            "IVA total: 21.00",
            "Total: 121.00",
            "ITEM|Servicio tecnico|1|100.00|21|0"
        ]);

        var document = new PdfDocumentReader().Read(path);
        var draft = new SupplierInvoiceDraftBuilder().Build(document, 321);

        Assert.True(document.IsSuccess);
        Assert.Equal("SYNTH-2026-001", draft.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 8, 6), draft.InvoiceDate);
        Assert.Single(draft.Items);
        Assert.Equal(121m, draft.CalculatedTotal);
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

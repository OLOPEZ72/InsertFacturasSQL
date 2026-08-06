using InsertFacturasSQL.Models;
using InsertFacturasSQL.Services;

namespace InsertFacturasSQL.Tests;

public sealed class SupplierInvoiceDraftBuilderTests
{
    [Fact]
    public void Build_ParsesSyntheticInvoiceAndCalculatesTotals()
    {
        var document = CreateSyntheticDocument("""
Proveedor: Proveedor de prueba
CIF: B12345678
Factura: F-2026-100
Fecha: 06/08/2026
Descripción: Servicios profesionales
Moneda: EUR
Subtotal: 200.00
IVA total: 42.00
Total: 242.00
ITEM|Análisis técnico|2|100.00|21|0
""");
        var builder = new SupplierInvoiceDraftBuilder();

        SupplierInvoiceDraft draft = builder.Build(document, 321);

        Assert.Equal("Proveedor de prueba", draft.ProviderName);
        Assert.Equal("F-2026-100", draft.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 8, 6), draft.InvoiceDate);
        Assert.Equal("Factura F-2026-100", draft.Notes);
        Assert.Equal(
            "Proveedor de prueba - Factura F-2026-100 - Servicios profesionales",
            draft.InitDescription);
        var item = Assert.Single(draft.Items);
        Assert.Equal(2m, item.Amount);
        Assert.Equal(100m, item.UnitPrice);
        Assert.Equal(21m, item.IVA);
        Assert.Equal(200m, draft.CalculatedSubtotal);
        Assert.Equal(42m, draft.CalculatedTaxTotal);
        Assert.Equal(242m, draft.CalculatedTotal);
    }

    [Fact]
    public void Validate_RejectsInvalidAmountsAndMismatchedDocumentTotal()
    {
        var document = CreateSyntheticDocument("""
Proveedor: Proveedor de prueba
Factura: F-INVALIDA
Fecha: 06/08/2026
Total: 50.00
ITEM|Línea inválida|0|-1|150
""");
        var builder = new SupplierInvoiceDraftBuilder();
        SupplierInvoiceDraft draft = builder.Build(document, 321);
        draft.CompanyId = 123;
        draft.CommercialProjectIsValid = true;

        builder.Validate(draft);

        Assert.Contains(draft.ValidationErrors, error => error.Contains("Amount", StringComparison.Ordinal));
        Assert.Contains(draft.ValidationErrors, error => error.Contains("UnitPrice", StringComparison.Ordinal));
        Assert.Contains(draft.ValidationErrors, error => error.Contains("IVA", StringComparison.Ordinal));
        Assert.Contains(draft.ValidationErrors, error => error.Contains("total calculado", StringComparison.OrdinalIgnoreCase));
    }

    private static DocumentReadResult CreateSyntheticDocument(string text) => new()
    {
        FileName = "synthetic.pdf",
        FilePath = "synthetic.pdf",
        ExtractedText = text,
        PageCount = 1,
        IsSuccess = true
    };
}

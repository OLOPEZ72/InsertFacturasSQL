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

    [Fact]
    public void Build_ParsesSpanishIssuerInvoiceWithReceiverAndEuropeanAmounts()
    {
        var document = CreateSyntheticDocument("""
FACTURA
Example AI OpCo, LLC
EU OSS VAT: EU123456789
Número de factura: SYNTH-ABC-0031
Fecha de emisión: 1 de agosto de 2026
Fecha de vencimiento: 1 de agosto de 2026
Facturar a
Example Client SA
CIF: ESA12345678
Moneda: US$
Descripción
Business Subscription (por seat)
Periodo: 1 ago 20261 sept 2026
Cantidad: 5
Precio unitario: 25,00 US$
IVA: 21 %
Importe base: 125,00 US$
Total sin impuestos: 125,00 US$
Subtotal: 125,00 US$
IVA total: 26,25 US$
Total: 151,25 US$
Importe adeudado: 151,25 US$
""");

        SupplierInvoiceDraft draft = new SupplierInvoiceDraftBuilder().Build(document, 321);

        Assert.Equal("Example AI OpCo, LLC", draft.ProviderName);
        Assert.NotEqual("Example Client SA", draft.ProviderName);
        Assert.Equal("EU123456789", draft.SupplierTaxId);
        Assert.Equal("SYNTH-ABC-0031", draft.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 8, 1), draft.InvoiceDate);
        Assert.Equal(new DateTime(2026, 8, 1), draft.DueDate);
        Assert.Equal("USD", draft.CurrencyCode);

        SupplierInvoiceItemDraft item = Assert.Single(draft.Items);
        Assert.Equal("Business Subscription (por seat) - 1 ago 20261 sept 2026", item.Description);
        Assert.Equal(5m, item.Amount);
        Assert.Equal(25m, item.UnitPrice);
        Assert.Equal(21m, item.IVA);
        Assert.Equal(125m, item.DocumentLineNetAmount);
        Assert.Equal(125m, item.CalculatedNetAmount);

        Assert.Equal(125m, draft.DocumentSubtotal);
        Assert.Equal(26.25m, draft.DocumentTaxTotal);
        Assert.Equal(151.25m, draft.DocumentTotal);
    }

    [Fact]
    public void Build_PrefersAmountDueAndDoesNotUseTaxExclusiveTotalAsFinalTotal()
    {
        var document = CreateSyntheticDocument("""
Proveedor: Example GmbH
VAT ID: EU123456789
Número de factura: INV-ABC-123
Fecha de emisión: 1 de septiembre de 2026
Descripción: Servicio
Cantidad: 1
Precio unitario: 100,00 EUR
IVA: 21 %
Importe base: 100,00 EUR
Total sin impuestos: 100,00 EUR
Subtotal: 100,00 EUR
IVA total: 21,00 EUR
Total: 120,00 EUR
Importe adeudado: 121,00 EUR
""");

        SupplierInvoiceDraft draft = new SupplierInvoiceDraftBuilder().Build(document, 321);

        Assert.Equal(121m, draft.DocumentTotal);
        Assert.NotEqual(100m, draft.DocumentTotal);
        Assert.Equal("EUR", draft.CurrencyCode);
    }

    [Theory]
    [InlineData("1 de enero de 2026", 1)]
    [InlineData("1 de febrero de 2026", 2)]
    [InlineData("1 de marzo de 2026", 3)]
    [InlineData("1 de abril de 2026", 4)]
    [InlineData("1 de mayo de 2026", 5)]
    [InlineData("1 de junio de 2026", 6)]
    [InlineData("1 de julio de 2026", 7)]
    [InlineData("1 de agosto de 2026", 8)]
    [InlineData("1 de septiembre de 2026", 9)]
    [InlineData("1 de octubre de 2026", 10)]
    [InlineData("1 de noviembre de 2026", 11)]
    [InlineData("1 de diciembre de 2026", 12)]
    public void Build_ParsesEverySpanishMonthName(string date, int expectedMonth)
    {
        var document = CreateSyntheticDocument($"""
Proveedor: Example Ltd
Factura: INV-2026-01
Fecha de emisión: {date}
ITEM|Servicio|1|1|0|0
""");

        SupplierInvoiceDraft draft = new SupplierInvoiceDraftBuilder().Build(document, 321);

        Assert.Equal(expectedMonth, draft.InvoiceDate.Month);
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

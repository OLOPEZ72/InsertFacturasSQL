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

    [Fact]
    public void Build_DoesNotTreatCustomerNameOrCifAsIssuer()
    {
        var document = CreateSyntheticDocument("""
FACTURA
Carrefour
Proveedor: Carrefour
Cliente
NOMBRE: Ibys Technologies SA
CIF: A79286506
Factura: CAR-2026-1
Fecha: 06/08/2026
ITEM|Compra|1|10|21|10
""");

        SupplierInvoiceDraft draft = new SupplierInvoiceDraftBuilder().Build(document, 321);

        Assert.Equal("Carrefour", draft.ProviderName);
        Assert.Null(draft.SupplierTaxId);
        Assert.Contains("Ibys Technologies SA", draft.RecipientCandidateBlock);
        Assert.Contains("cabecera", draft.IssuerSelectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_ParsesRetailTableLabeledDateAndTaxSummaryWithoutGuessingProvider()
    {
        var document = CreateSyntheticDocument("""
FECHA FACTURA: 14/04/2026
FECHA PEDIDO: 13/04/2026
8431876011937 LECHE SEMI CRF 1L 60,000 52,80 0,00 52,80 50,77 4,00
IVA 4,00 50,77 2,03 0,00 0,00 52,80
TOTAL IMPUESTOS INCLUIDOS 52,80 EUR
BASE IMPONIBLE GASTOS DE ENVÍO 3,30 EUR
""");

        SupplierInvoiceDraft draft = new SupplierInvoiceDraftBuilder().Build(document, 321);
        SupplierInvoiceItemDraft item = Assert.Single(draft.Items);

        Assert.Equal(new DateTime(2026, 4, 14), draft.InvoiceDate);
        Assert.Equal("LECHE SEMI CRF 1L", item.Description);
        Assert.Equal(60m, item.Amount);
        Assert.Equal(4m, item.IVA);
        Assert.Equal(50.77m, item.DocumentLineNetAmount);
        Assert.Equal(52.80m, item.DocumentLineTotal);
        Assert.Equal(50.77m, draft.DocumentSubtotal);
        Assert.Equal(2.03m, draft.DocumentTaxTotal);
        Assert.Equal(52.80m, draft.DocumentTotal);
        Assert.Empty(draft.ProviderName);
        Assert.Contains("imagen o logotipo", draft.Warnings.Single(w => w.Contains("imagen", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Validate_ReconcilesRetailLinesAndAllowsRoundedNumericUnitPrice()
    {
        var draft = new SupplierInvoiceDraft
        {
            CompanyId = 1,
            CommercialProjectId = 1970,
            CommercialProjectIsValid = true,
            InvoiceNumber = "RETAIL-1",
            InvoiceDate = new DateTime(2026, 4, 14),
            DocumentSubtotal = 50.77m,
            DocumentTaxTotal = 6.02m,
            DocumentTotal = 56.79m,
            Items =
            [
                new SupplierInvoiceItemDraft { Position = 1, Description = "Leche", Amount = 60, UnitPrice = 50.77m / 60m, IVA = 4, CommercialProjectId = 1970 },
                new SupplierInvoiceItemDraft { Position = 2, Description = "Envío", Amount = 1, UnitPrice = 3.30m, IVA = 21, CommercialProjectId = 1970 }
            ]
        };

        new SupplierInvoiceDraftBuilder().Validate(draft);

        Assert.Equal(54.07m, draft.CalculatedSubtotal);
        Assert.Equal(2.72m, draft.CalculatedTaxTotal);
        Assert.Equal(56.79m, draft.CalculatedTotal);
        Assert.Equal(54.07m, draft.DocumentSubtotal);
        Assert.Equal(2.72m, draft.DocumentTaxTotal);
        Assert.DoesNotContain(draft.ValidationErrors, error => error.Contains("UnitPrice no cabe", StringComparison.Ordinal));
        Assert.DoesNotContain(draft.ValidationErrors, error => error.Contains("subtotal calculado", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(draft.ValidationErrors, error => error.Contains("IVA calculado", StringComparison.OrdinalIgnoreCase));
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

using InsertFacturasSQL.Models;
using InsertFacturasSQL.Services;

namespace InsertFacturasSQL.Tests;

public sealed class SupplierInvoiceValidationReportTests
{
    [Fact]
    public void Build_ReportsAllSectionsAndConfidence()
    {
        var draft = new SupplierInvoiceDraft
        {
            CompanyId = 10,
            InvoiceNumber = "INV-1",
            InvoiceDate = new DateTime(2026, 8, 1),
            CurrencyCode = "EUR",
            DetectedCurrencyId = 1,
            CurrencyId = 1,
            CommercialProjectId = 20,
            CommercialProjectIsValid = true,
            DocumentSubtotal = 10m,
            DocumentTaxTotal = 2.1m,
            DocumentTotal = 12.1m,
            Items = [new SupplierInvoiceItemDraft { Description = "Servicio", Amount = 1, UnitPrice = 10, IVA = 21 }]
        };

        ValidationReportResult result = SupplierInvoiceValidationReport.Build(draft, true);

        Assert.Contains("INFORME DE VALIDACIÓN", result.Text);
        Assert.Contains("Proveedor: OK", result.Text);
        Assert.Contains("CompanyID: OK", result.Text);
        Assert.Contains("Totales: OK", result.Text);
        Assert.Contains("SQL Preview: OK", result.Text);
        Assert.Equal(100, result.ConfidencePercentage);
    }

    [Fact]
    public void Build_MarksMissingReferencesAsErrors()
    {
        var draft = new SupplierInvoiceDraft();
        ValidationReportResult result = SupplierInvoiceValidationReport.Build(draft, false);

        Assert.Contains("Proveedor: ERROR", result.Text);
        Assert.Contains("Proyecto: ERROR", result.Text);
        Assert.Contains("SQL Preview: ERROR", result.Text);
        Assert.True(result.ConfidencePercentage < 50);
    }
}

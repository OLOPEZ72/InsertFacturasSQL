using InsertFacturasSQL.Models;
using InsertFacturasSQL.Services;

namespace InsertFacturasSQL.Tests;

public sealed class SupplierInvoiceReviewEngineTests
{
    [Fact]
    public void ReviewEdits_UpdateInvoiceDateAndNumberAndShowDifferences()
    {
        var draft = new SupplierInvoiceDraft { InvoiceNumber = "LOCAL-1", InvoiceDate = new DateTime(2026, 1, 1) };
        var engine = new SupplierInvoiceReviewEngine();
        engine.CaptureOriginal(draft);

        Assert.True(engine.SetInvoiceNumber(draft, "CORRECT-2"));
        Assert.True(engine.SetDate(draft, "14/04/2026"));

        Assert.Contains(engine.Differences(draft), difference => difference.Contains("CORRECT-2", StringComparison.Ordinal));
        Assert.Equal(new DateTime(2026, 4, 14), draft.InvoiceDate);
    }

    [Fact]
    public void ReviewEdits_UpdateItemsAndRevalidationSeesNewTotals()
    {
        var draft = new SupplierInvoiceDraft
        {
            CommercialProjectId = 1970,
            CommercialProjectIsValid = true,
            Items = [new SupplierInvoiceItemDraft { Position = 1, Description = "Servicio", Amount = 1, UnitPrice = 10, IVA = 21, CommercialProjectId = 1970 }]
        };
        var engine = new SupplierInvoiceReviewEngine();

        Assert.True(engine.SetItem(draft, 1, "quantity", "2"));
        Assert.True(engine.SetItem(draft, 1, "price", "12.5"));
        Assert.True(engine.SetItem(draft, 1, "iva", "10"));

        new SupplierInvoiceDraftBuilder().Validate(draft);
        Assert.Equal(25m, draft.CalculatedSubtotal);
        Assert.Equal(2.5m, draft.CalculatedTaxTotal);
        Assert.DoesNotContain(draft.ValidationErrors, error => error.Contains("Amount", StringComparison.Ordinal));
    }

    [Fact]
    public void ReviewEdits_ProviderAndCurrencyUseValidatedMatches()
    {
        var draft = new SupplierInvoiceDraft();
        var engine = new SupplierInvoiceReviewEngine();

        engine.SetProvider(draft, new ProviderMatch(759, "CARREFOUR", "ManualId"));
        engine.SetCurrency(draft, new CurrencyMatch(1, "EUR", "Euro", "€"));

        Assert.Equal(759, draft.CompanyId);
        Assert.Equal(1, draft.CurrencyId);
        Assert.Equal("EUR", draft.CurrencyCode);
    }
}

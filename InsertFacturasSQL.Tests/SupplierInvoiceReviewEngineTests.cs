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
    public void ReviewEdits_ClearsPrintedAmountsWhenCorrectingPrice()
    {
        var draft = new SupplierInvoiceDraft
        {
            Items = [new SupplierInvoiceItemDraft
            {
                Position = 1, Description = "Leche", Amount = 60, UnitPrice = 0.88m,
                IVA = 4, DocumentLineNetAmount = 50.77m, DocumentLineTotal = 52.80m
            }]
        };

        Assert.True(new SupplierInvoiceReviewEngine().SetItem(draft, 1, "price", "0.846"));

        Assert.Null(draft.Items[0].DocumentLineNetAmount);
        Assert.Equal(0.846m, draft.Items[0].UnitPrice);
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

    [Fact]
    public void ReviewFlow_PreservesAiSupplierAndNeverUsesCustomer()
    {
        var draft = new SupplierInvoiceDraft { ProviderName = "Ibys Technologies SA" };
        OpenAiInvoiceExtractor.MergeIntoDraft(draft, new AiInvoiceExtraction
        {
            SupplierName = "Carrefour",
            SupplierTaxId = "FR123",
            SupplierEvidence = "Logotipo y cabecera superior",
            CustomerName = "Ibys Technologies SA",
            CustomerTaxId = "ES456"
        });
        var engine = new SupplierInvoiceReviewEngine();
        engine.CaptureOriginal(draft);

        Assert.Equal("Carrefour", draft.ProviderName);
        Assert.DoesNotContain(engine.Differences(draft), difference => difference.Contains("Ibys", StringComparison.OrdinalIgnoreCase));
    }
}

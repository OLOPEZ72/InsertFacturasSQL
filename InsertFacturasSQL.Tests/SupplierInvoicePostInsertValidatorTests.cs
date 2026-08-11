using InsertFacturasSQL.Services;

namespace InsertFacturasSQL.Tests;

public sealed class SupplierInvoicePostInsertValidatorTests
{
    private readonly SupplierInvoicePostInsertValidator _validator = new();

    [Fact]
    public void Calculate_AppliesDiscountToPersistedItem()
    {
        var totals = _validator.Calculate([new PersistedSupplierInvoiceItem(10m, 10m, 10m, 21m)]);

        Assert.Equal(90m, totals.BaseTotal);
        Assert.Equal(18.90m, totals.TaxTotal);
        Assert.Equal(108.90m, totals.FinalTotal);
    }

    [Fact]
    public void Calculate_UsesRoundedPersistedUnitPrice()
    {
        var totals = _validator.Calculate([new PersistedSupplierInvoiceItem(60m, 0.846m, null, 4m)]);

        Assert.Equal(50.76m, totals.BaseTotal);
        Assert.Equal(2.03m, totals.TaxTotal);
    }

    [Fact]
    public void Calculate_IncludesShippingAsAnItem()
    {
        var totals = _validator.Calculate([new PersistedSupplierInvoiceItem(1m, 3.30m, null, 21m)]);

        Assert.Equal(3.30m, totals.BaseTotal);
        Assert.Equal(0.69m, totals.TaxTotal);
        Assert.Equal(3.99m, totals.FinalTotal);
    }

    [Fact]
    public void Validate_ReturnsValidWhenTotalMatches()
    {
        var result = _validator.Validate([new PersistedSupplierInvoiceItem(1m, 50m, null, 0m)], 50m);

        Assert.True(result.IsValid);
        Assert.Equal(0m, result.Difference);
    }

    [Fact]
    public void Validate_AllowsDifferenceWithinTolerance()
    {
        var result = _validator.Validate([new PersistedSupplierInvoiceItem(1m, 50m, null, 0m)], 50.02m);

        Assert.True(result.IsValid);
        Assert.Equal(0.02m, result.Difference);
    }

    [Fact]
    public void Validate_RejectsDifferenceOutsideTolerance()
    {
        var result = _validator.Validate([new PersistedSupplierInvoiceItem(1m, 50m, null, 0m)], 50.03m);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }
}

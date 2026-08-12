namespace InsertFacturasSQL.Models;

public sealed class SupplierInvoiceItemDraft
{
    public int Position { get; init; }
    public string Description { get; init; } = "";
    public decimal Amount { get; init; }
    public decimal UnitPrice { get; init; }
    public bool UnitPriceCalculated { get; init; }
    public decimal? DiscountPercent { get; init; }
    public decimal IVA { get; init; }
    public decimal? DocumentLineNetAmount { get; init; }
    public decimal? DocumentLineTaxAmount { get; init; }
    public decimal? DocumentLineTotal { get; init; }
    public int? CommercialProjectId { get; set; }

    public decimal CalculatedNetAmount => DocumentLineNetAmount ?? RoundCurrency(
        Amount * UnitPrice * (1m - (DiscountPercent ?? 0m) / 100m));

    public decimal CalculatedTaxAmount => DocumentLineTaxAmount ?? RoundCurrency(CalculatedNetAmount * IVA / 100m);

    public decimal CalculatedTotal => DocumentLineTotal ?? CalculatedNetAmount + CalculatedTaxAmount;

    // ItemsProviderOrder.UnitPrice is numeric(18,3); this is the value SQL Server
    // will actually use when calculating the persisted line base.
    public decimal PersistedBaseAmount => RoundCurrency(
        Amount * PersistedUnitPrice *
        (1m - (DiscountPercent ?? 0m) / 100m));

    public decimal PersistedUnitPrice => decimal.Round(UnitPrice, 3, MidpointRounding.AwayFromZero);

    public decimal PersistedBaseDifference => DocumentLineNetAmount.HasValue
        ? Math.Abs(PersistedBaseAmount - DocumentLineNetAmount.Value)
        : 0m;

    private static decimal RoundCurrency(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

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

    public decimal CalculatedNetAmount => RoundCurrency(
        Amount * UnitPrice * (1m - (DiscountPercent ?? 0m) / 100m));

    public decimal CalculatedTaxAmount => RoundCurrency(CalculatedNetAmount * IVA / 100m);

    public decimal CalculatedTotal => CalculatedNetAmount + CalculatedTaxAmount;

    private static decimal RoundCurrency(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

namespace InsertFacturasSQL.Services;

public sealed record PersistedSupplierInvoiceItem(decimal Amount, decimal UnitPrice, decimal? DiscountPercent, decimal IVA, bool Disabled = false);
public sealed record SupplierInvoicePersistedTotals(decimal BaseTotal, decimal TaxTotal, decimal FinalTotal);
public sealed record SupplierInvoicePostInsertValidation(bool IsValid, decimal Difference, SupplierInvoicePersistedTotals Totals, string? Error);

public sealed class SupplierInvoicePostInsertValidator : ISupplierInvoicePostInsertValidator
{
    public const decimal DefaultTolerance = 0.02m;

    public SupplierInvoicePersistedTotals Calculate(IEnumerable<PersistedSupplierInvoiceItem> items)
    {
        decimal baseTotal = 0m;
        decimal taxTotal = 0m;
        foreach (PersistedSupplierInvoiceItem item in items.Where(item => !item.Disabled))
        {
            decimal lineBase = decimal.Round(item.Amount * item.UnitPrice * (1m - (item.DiscountPercent ?? 0m) / 100m), 2, MidpointRounding.AwayFromZero);
            decimal lineTax = decimal.Round(lineBase * item.IVA / 100m, 2, MidpointRounding.AwayFromZero);
            baseTotal += lineBase;
            taxTotal += lineTax;
        }
        baseTotal = decimal.Round(baseTotal, 2, MidpointRounding.AwayFromZero);
        taxTotal = decimal.Round(taxTotal, 2, MidpointRounding.AwayFromZero);
        return new SupplierInvoicePersistedTotals(baseTotal, taxTotal, baseTotal + taxTotal);
    }

    public SupplierInvoicePostInsertValidation Validate(IEnumerable<PersistedSupplierInvoiceItem> items, decimal expectedTotal, decimal tolerance = DefaultTolerance)
    {
        SupplierInvoicePersistedTotals totals = Calculate(items);
        decimal difference = Math.Abs(totals.FinalTotal - expectedTotal);
        return new SupplierInvoicePostInsertValidation(
            difference <= tolerance,
            difference,
            totals,
            difference <= tolerance ? null : $"El total persistido ({totals.FinalTotal:F2}) difiere del documento ({expectedTotal:F2}) por {difference:F2}.");
    }
}

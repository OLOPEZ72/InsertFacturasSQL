namespace InsertFacturasSQL.Services;

public interface ISupplierInvoicePostInsertValidator
{
    SupplierInvoicePersistedTotals Calculate(IEnumerable<PersistedSupplierInvoiceItem> items);

    SupplierInvoicePostInsertValidation Validate(
        IEnumerable<PersistedSupplierInvoiceItem> items,
        decimal expectedTotal,
        decimal tolerance = SupplierInvoicePostInsertValidator.DefaultTolerance);
}

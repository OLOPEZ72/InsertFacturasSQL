namespace InsertFacturasSQL.Models;

public sealed class AiInvoiceExtraction
{
    public string? ProviderName { get; set; }
    public string? SupplierTaxId { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateTime? InvoiceDate { get; set; }
    public DateTime? DueDate { get; set; }
    public string? CurrencyCode { get; set; }
    public string? MainDescription { get; set; }
    public decimal? DocumentSubtotal { get; set; }
    public decimal? DocumentTaxTotal { get; set; }
    public decimal? DocumentTotal { get; set; }
    public List<AiInvoiceItemExtraction> Items { get; set; } = [];
}

public sealed class AiInvoiceItemExtraction
{
    public string? Description { get; set; }
    public decimal? Amount { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? IVA { get; set; }
    public decimal? DocumentLineNetAmount { get; set; }
    public decimal? DocumentLineTotal { get; set; }
}

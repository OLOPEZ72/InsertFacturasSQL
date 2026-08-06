namespace InsertFacturasSQL.Models;

public sealed class SupplierInvoiceDraft
{
    public string? SupplierTaxId { get; init; }
    public string ProviderName { get; set; } = "";
    public string InvoiceNumber { get; init; } = "";
    public DateTime InvoiceDate { get; init; }
    public DateTime? DueDate { get; init; }
    public string MainDescription { get; init; } = "";
    public string? CurrencyCode { get; init; }
    public DateTime? PaymentDate { get; init; }
    public string? PaymentNotes { get; init; }
    public decimal? DocumentSubtotal { get; init; }
    public decimal? DocumentTaxTotal { get; init; }
    public decimal? DocumentTotal { get; init; }

    public int? CompanyId { get; set; }
    public bool ProviderConfirmedManually { get; set; }
    public bool CurrencyConfirmedManually { get; set; }
    public bool ProjectConfirmedManually { get; set; }
    public int? CommercialProjectId { get; set; }
    public string? CommercialProjectName { get; set; }
    public bool CommercialProjectIsValid { get; set; }
    public string? CommercialProjectValidationError { get; set; }
    public int? DetectedCurrencyId { get; set; }
    public string? ConfiguredCurrencyCode { get; set; }
    public int PotentialDuplicateCount { get; set; }

    public int UserId { get; init; } = 5;
    public int SendedModeId { get; init; } = 1;
    public int CreditCardId { get; init; } = 124;
    public int PaymentMethodId { get; init; } = 400;
    public int CurrencyId { get; set; } = 1;
    public bool Paid { get; init; }
    public bool Charget { get; init; }

    public List<SupplierInvoiceItemDraft> Items { get; init; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> ValidationErrors { get; } = [];
    public List<string> ProviderCandidates { get; } = [];

    public string Notes => $"Factura {InvoiceNumber}";

    public string InitDescription =>
        $"{ProviderName} - Factura {InvoiceNumber} - {MainDescription}";

    public decimal CalculatedSubtotal => Items.Sum(item => item.CalculatedNetAmount);
    public decimal CalculatedTaxTotal => Items.Sum(item => item.CalculatedTaxAmount);
    public decimal CalculatedTotal => Items.Sum(item => item.CalculatedTotal);
    public bool IsValid => ValidationErrors.Count == 0;
}

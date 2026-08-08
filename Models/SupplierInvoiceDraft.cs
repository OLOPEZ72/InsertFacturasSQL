namespace InsertFacturasSQL.Models;

public sealed class SupplierInvoiceDraft
{
    public string? SupplierTaxId { get; set; }
    public string ProviderName { get; set; } = "";
    public string IssuerCandidateBlock { get; set; } = "";
    public string RecipientCandidateBlock { get; set; } = "";
    public string IssuerSelectionReason { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public DateTime InvoiceDate { get; set; }
    public DateTime? DueDate { get; set; }
    public string MainDescription { get; set; } = "";
    public string? CurrencyCode { get; set; }
    public DateTime? PaymentDate { get; init; }
    public string? PaymentNotes { get; init; }
    public decimal? DocumentSubtotal { get; set; }
    public decimal? DocumentTaxTotal { get; set; }
    public decimal? DocumentTotal { get; set; }

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

    public List<SupplierInvoiceItemDraft> Items { get; set; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> ValidationErrors { get; } = [];
    public List<string> ProviderCandidates { get; } = [];
    public Dictionary<string, string> FieldOrigins { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ManualNotes { get; set; }
    public string? ManualInitDescription { get; set; }

    public string Notes => ManualNotes ?? $"Factura {InvoiceNumber}";

    public string InitDescription => ManualInitDescription ??
        $"{ProviderName} - Factura {InvoiceNumber} - {MainDescription}";

    public decimal CalculatedSubtotal => Items.Sum(item => item.CalculatedNetAmount);
    public decimal CalculatedTaxTotal => Items.Sum(item => item.CalculatedTaxAmount);
    public decimal CalculatedTotal => Items.Sum(item => item.CalculatedTotal);
    public bool IsValid => ValidationErrors.Count == 0;
}

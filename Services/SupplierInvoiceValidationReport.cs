using System.Text;
using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public sealed record ValidationReportResult(string Text, int ConfidencePercentage);

public static class SupplierInvoiceValidationReport
{
    public static ValidationReportResult Build(SupplierInvoiceDraft draft, bool sqlPreviewAvailable)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var sections = new List<(string Name, string Status)> {
            ("Proveedor", draft.CompanyId is > 0 ? "OK" : "ERROR"),
            ("CompanyID", draft.CompanyId is > 0 ? "OK" : "ERROR"),
            ("Número de factura", string.IsNullOrWhiteSpace(draft.InvoiceNumber) ? "ERROR" : "OK"),
            ("Fecha", draft.InvoiceDate >= new DateTime(1900, 1, 1) && draft.InvoiceDate <= new DateTime(2079, 6, 6) ? "OK" : "ERROR"),
            ("Moneda", GetCurrencyStatus(draft)),
            ("Proyecto", draft.CommercialProjectIsValid ? "OK" : "ERROR"),
            ("Items", draft.Items.Count == 0 || draft.Items.Any(item => item.Amount <= 0 || item.UnitPrice < 0) ? "ERROR" : "OK"),
            ("Totales", GetTotalsStatus(draft)),
            ("SQL Preview", sqlPreviewAvailable ? "OK" : "ERROR")
        };

        int confidence = (int)Math.Round(sections.Average(section => section.Status switch
        {
            "OK" => 100d,
            "WARNING" => 60d,
            _ => 0d
        }));
        var output = new StringBuilder();
        output.AppendLine();
        output.AppendLine("INFORME DE VALIDACIÓN");
        foreach (var section in sections)
            output.AppendLine($"{section.Name}: {section.Status}");
        output.AppendLine($"Confianza del documento: {confidence}%");
        return new ValidationReportResult(output.ToString(), confidence);
    }

    private static string GetCurrencyStatus(SupplierInvoiceDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.CurrencyCode)) return "WARNING";
        if (draft.DetectedCurrencyId is null) return "WARNING";
        return draft.DetectedCurrencyId == draft.CurrencyId ? "OK" : "WARNING";
    }

    private static string GetTotalsStatus(SupplierInvoiceDraft draft)
    {
        if (draft.DocumentSubtotal is null || draft.DocumentTaxTotal is null || draft.DocumentTotal is null)
            return "WARNING";
        return Math.Abs(draft.CalculatedSubtotal - draft.DocumentSubtotal.Value) <= 0.02m &&
               Math.Abs(draft.CalculatedTaxTotal - draft.DocumentTaxTotal.Value) <= 0.02m &&
               Math.Abs(draft.CalculatedTotal - draft.DocumentTotal.Value) <= 0.02m ? "OK" : "ERROR";
    }
}

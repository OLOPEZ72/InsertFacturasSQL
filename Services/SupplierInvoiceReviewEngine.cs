using System.Globalization;
using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public sealed class SupplierInvoiceReviewEngine
{
    private readonly Dictionary<string, string> originalValues = new(StringComparer.OrdinalIgnoreCase);

    public void CaptureOriginal(SupplierInvoiceDraft draft)
    {
        originalValues["Proveedor"] = draft.ProviderName;
        originalValues["Número de factura"] = draft.InvoiceNumber;
        originalValues["Fecha"] = draft.InvoiceDate == DateTime.MinValue ? "" : draft.InvoiceDate.ToString("dd/MM/yyyy");
        originalValues["Moneda"] = draft.CurrencyCode ?? "";
        originalValues["Notes"] = draft.Notes;
        originalValues["InitDescription"] = draft.InitDescription;
    }

    public void SetProvider(SupplierInvoiceDraft draft, ProviderMatch match)
    {
        draft.CompanyId = match.CompanyID;
        draft.ProviderName = match.CompanyName;
        draft.ProviderConfirmedManually = true;
    }

    public void SetCurrency(SupplierInvoiceDraft draft, CurrencyMatch match)
    {
        draft.CurrencyId = match.CurrencyID;
        draft.CurrencyCode = match.Code;
        draft.ConfiguredCurrencyCode = match.Code;
        draft.CurrencyConfirmedManually = true;
    }

    public bool SetInvoiceNumber(SupplierInvoiceDraft draft, string value) => SetText(value, text => draft.InvoiceNumber = text);
    public bool SetDate(SupplierInvoiceDraft draft, string value)
    {
        if (!DateTime.TryParseExact(value, ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)) return false;
        draft.InvoiceDate = date;
        return true;
    }
    public void SetNotes(SupplierInvoiceDraft draft, string value) => draft.ManualNotes = value;
    public void SetInitDescription(SupplierInvoiceDraft draft, string value) => draft.ManualInitDescription = value;

    public bool SetItem(SupplierInvoiceDraft draft, int position, string field, string value)
    {
        SupplierInvoiceItemDraft? item = draft.Items.FirstOrDefault(candidate => candidate.Position == position);
        if (item is null) return false;
        switch (field.ToLowerInvariant())
        {
            case "description":
                draft.Items[position - 1] = Copy(item, item.Amount, item.UnitPrice, item.IVA, value, false);
                return true;
            case "quantity" when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal quantity):
                draft.Items[position - 1] = Copy(item, quantity, item.UnitPrice, item.IVA, item.Description, true); return true;
            case "price" when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price):
                draft.Items[position - 1] = Copy(item, item.Amount, price, item.IVA, item.Description, true); return true;
            case "iva" when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal iva):
                draft.Items[position - 1] = Copy(item, item.Amount, item.UnitPrice, iva, item.Description, true); return true;
            default: return false;
        }
    }

    public IReadOnlyList<string> Differences(SupplierInvoiceDraft draft) => originalValues
        .Where(pair => pair.Value != CurrentValue(draft, pair.Key))
        .Select(pair => $"{pair.Key}: detectado='{pair.Value}' | corregido='{CurrentValue(draft, pair.Key)}'")
        .ToList();

    private static string CurrentValue(SupplierInvoiceDraft draft, string key) => key switch
    {
        "Proveedor" => draft.ProviderName,
        "Número de factura" => draft.InvoiceNumber,
        "Fecha" => draft.InvoiceDate == DateTime.MinValue ? "" : draft.InvoiceDate.ToString("dd/MM/yyyy"),
        "Moneda" => draft.CurrencyCode ?? "",
        "Notes" => draft.Notes,
        "InitDescription" => draft.InitDescription,
        _ => ""
    };

    private static bool SetText(string value, Action<string> setter)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        setter(value.Trim());
        return true;
    }

    private static SupplierInvoiceItemDraft Copy(SupplierInvoiceItemDraft item, decimal amount, decimal price, decimal iva, string description, bool recalculateAmounts) => new()
    {
        Position = item.Position, Description = description, Amount = amount, UnitPrice = price, IVA = iva,
        DiscountPercent = item.DiscountPercent,
        DocumentLineNetAmount = recalculateAmounts ? null : item.DocumentLineNetAmount,
        DocumentLineTaxAmount = recalculateAmounts ? null : item.DocumentLineTaxAmount,
        DocumentLineTotal = recalculateAmounts ? null : item.DocumentLineTotal,
        CommercialProjectId = item.CommercialProjectId, UnitPriceCalculated = false
    };
}

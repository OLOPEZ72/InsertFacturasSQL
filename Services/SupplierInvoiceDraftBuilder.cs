using System.Globalization;
using System.Text.RegularExpressions;
using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public sealed partial class SupplierInvoiceDraftBuilder
{
    private const decimal TotalTolerance = 0.02m;

    public SupplierInvoiceDraft Build(DocumentReadResult document, int commercialProjectId)
    {
        ArgumentNullException.ThrowIfNull(document);

        string text = document.ExtractedText ?? "";
        string providerName = ReadLabel(text, "Proveedor", "Supplier") ?? "";
        string? taxId = ReadLabel(text, "CIF", "NIF", "VAT", "Tax ID");
        string invoiceNumber = ReadLabel(
            text,
            "Número de factura",
            "Numero de factura",
            "Invoice number",
            "Invoice no",
            "Factura") ?? "";
        string mainDescription = ReadLabel(text, "Descripción", "Descripcion", "Description", "Concepto") ?? "";
        string? currencyCode = ReadLabel(text, "Moneda", "Currency");
        DateTime invoiceDate = TryParseDate(ReadLabel(text, "Fecha de factura", "Invoice date", "Fecha"));
        DateTime? paymentDate = TryParseNullableDate(ReadLabel(text, "Fecha de pago", "Payment date"));

        var draft = new SupplierInvoiceDraft
        {
            SupplierTaxId = taxId,
            ProviderName = providerName,
            InvoiceNumber = invoiceNumber,
            InvoiceDate = invoiceDate,
            MainDescription = mainDescription,
            CurrencyCode = currencyCode,
            PaymentDate = paymentDate,
            PaymentNotes = ReadLabel(text, "Notas de pago", "Payment notes"),
            DocumentSubtotal = TryParseNullableDecimal(ReadLabel(text, "Subtotal", "Base imponible")),
            DocumentTaxTotal = TryParseNullableDecimal(ReadLabel(text, "IVA total", "Tax total", "Impuestos")),
            DocumentTotal = TryParseNullableDecimal(ReadLabel(text, "Total factura", "Invoice total", "Total")),
            CommercialProjectId = commercialProjectId,
            Items = ParseItems(text, commercialProjectId)
        };

        if (draft.DocumentSubtotal is null || draft.DocumentTaxTotal is null || draft.DocumentTotal is null)
        {
            draft.Warnings.Add("El PDF no contiene todos los totales documentales necesarios para una reconciliación completa.");
        }

        Validate(draft);
        return draft;
    }

    public void Validate(SupplierInvoiceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.ValidationErrors.Clear();

        if (draft.CompanyId is null or <= 0)
        {
            draft.ValidationErrors.Add("Proveedor no resuelto.");
        }

        if (draft.CommercialProjectId is null or <= 0 || !draft.CommercialProjectIsValid)
        {
            draft.ValidationErrors.Add("Proyecto comercial no válido o no disponible para nuevas facturas.");
        }

        if (string.IsNullOrWhiteSpace(draft.InvoiceNumber))
        {
            draft.ValidationErrors.Add("Número de factura obligatorio.");
        }

        if (draft.InvoiceDate < new DateTime(1900, 1, 1) || draft.InvoiceDate > new DateTime(2079, 6, 6))
        {
            draft.ValidationErrors.Add("Fecha de factura ausente o fuera del rango smalldatetime.");
        }

        if (draft.Items.Count == 0)
        {
            draft.ValidationErrors.Add("La factura debe contener al menos un item.");
        }

        if (draft.Notes.Length > 250)
        {
            draft.ValidationErrors.Add("Notes supera los 250 caracteres.");
        }

        if (draft.InitDescription.Length > 900)
        {
            draft.ValidationErrors.Add("InitDescription supera los 900 caracteres.");
        }

        if (draft.PaymentNotes?.Length > 250)
        {
            draft.ValidationErrors.Add("PaymentNotes supera los 250 caracteres.");
        }

        foreach (var item in draft.Items)
        {
            string prefix = $"Item {item.Position}:";
            if (string.IsNullOrWhiteSpace(item.Description) || item.Description.Length > 250)
            {
                draft.ValidationErrors.Add($"{prefix} descripción obligatoria de hasta 250 caracteres.");
            }

            if (item.Amount <= 0)
            {
                draft.ValidationErrors.Add($"{prefix} Amount debe ser mayor que cero.");
            }
            else if (!FitsSqlDecimal(item.Amount, 18, 2))
            {
                draft.ValidationErrors.Add($"{prefix} Amount no cabe en numeric(18,2).");
            }

            if (item.UnitPrice < 0)
            {
                draft.ValidationErrors.Add($"{prefix} UnitPrice no puede ser negativo.");
            }
            else if (!FitsSqlDecimal(item.UnitPrice, 18, 3))
            {
                draft.ValidationErrors.Add($"{prefix} UnitPrice no cabe en numeric(18,3).");
            }

            if (item.IVA is < 0 or > 100)
            {
                draft.ValidationErrors.Add($"{prefix} IVA debe estar entre 0 y 100.");
            }
            else if (!FitsSqlDecimal(item.IVA, 18, 2))
            {
                draft.ValidationErrors.Add($"{prefix} IVA no cabe en numeric(18,2).");
            }

            if (item.DiscountPercent is < 0 or > 100)
            {
                draft.ValidationErrors.Add($"{prefix} descuento debe estar entre 0 y 100.");
            }
            else if (item.DiscountPercent.HasValue && !FitsSqlDecimal(item.DiscountPercent.Value, 18, 2))
            {
                draft.ValidationErrors.Add($"{prefix} descuento no cabe en numeric(18,2).");
            }

            if (item.CommercialProjectId is null or <= 0)
            {
                draft.ValidationErrors.Add($"{prefix} proyecto comercial obligatorio.");
            }
        }

        CompareTotal(draft, "subtotal", draft.DocumentSubtotal, draft.CalculatedSubtotal);
        CompareTotal(draft, "IVA", draft.DocumentTaxTotal, draft.CalculatedTaxTotal);
        CompareTotal(draft, "total", draft.DocumentTotal, draft.CalculatedTotal);

        if (draft.PotentialDuplicateCount > 0)
        {
            draft.ValidationErrors.Add("Se detectó una posible factura duplicada para el proveedor, número y fecha.");
        }
    }

    private static void CompareTotal(
        SupplierInvoiceDraft draft,
        string label,
        decimal? documentValue,
        decimal calculatedValue)
    {
        if (documentValue.HasValue && Math.Abs(documentValue.Value - calculatedValue) > TotalTolerance)
        {
            draft.ValidationErrors.Add(
                $"El {label} calculado ({calculatedValue:F2}) no coincide con el PDF ({documentValue.Value:F2}).");
        }
    }

    private static bool FitsSqlDecimal(decimal value, int precision, int scale)
    {
        decimal maximumExclusive = 1m;
        for (int index = 0; index < precision - scale; index++)
        {
            maximumExclusive *= 10m;
        }

        return value > -maximumExclusive &&
               value < maximumExclusive &&
               decimal.Round(value, scale) == value;
    }

    private static List<SupplierInvoiceItemDraft> ParseItems(string text, int commercialProjectId)
    {
        var items = new List<SupplierInvoiceItemDraft>();
        foreach (string rawLine in SplitLinesRegex().Split(text))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("ITEM|", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("ITEM;", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            char separator = line[4];
            string[] parts = line.Split(separator);
            if (parts.Length < 5 ||
                !TryParseDecimal(parts[2], out decimal amount) ||
                !TryParseDecimal(parts[3], out decimal unitPrice) ||
                !TryParseDecimal(parts[4], out decimal iva))
            {
                continue;
            }

            decimal? discount = parts.Length > 5 ? TryParseNullableDecimal(parts[5]) : null;
            items.Add(new SupplierInvoiceItemDraft
            {
                Position = items.Count + 1,
                Description = parts[1].Trim(),
                Amount = amount,
                UnitPrice = unitPrice,
                IVA = iva,
                DiscountPercent = discount,
                CommercialProjectId = commercialProjectId
            });
        }

        return items;
    }

    private static string? ReadLabel(string text, params string[] labels)
    {
        foreach (string line in SplitLinesRegex().Split(text))
        {
            foreach (string label in labels)
            {
                var match = Regex.Match(
                    line,
                    $@"^\s*{Regex.Escape(label)}\s*[:#-]\s*(.+?)\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim();
                }
            }
        }

        return null;
    }

    private static DateTime TryParseDate(string? value) =>
        TryParseNullableDate(value) ?? DateTime.MinValue;

    private static DateTime? TryParseNullableDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats = ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "d-M-yyyy"];
        foreach (CultureInfo culture in new[] { CultureInfo.GetCultureInfo("es-ES"), CultureInfo.InvariantCulture })
        {
            if (DateTime.TryParseExact(value.Trim(), formats, culture, DateTimeStyles.None, out DateTime exact) ||
                DateTime.TryParse(value.Trim(), culture, DateTimeStyles.None, out exact))
            {
                return exact.Date;
            }
        }

        return null;
    }

    private static decimal? TryParseNullableDecimal(string? value) =>
        TryParseDecimal(value, out decimal parsed) ? parsed : null;

    private static bool TryParseDecimal(string? value, out decimal parsed)
    {
        parsed = 0m;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = Regex.Replace(value.Trim(), @"[^0-9,\.\-]", "");
        int comma = normalized.LastIndexOf(',');
        int dot = normalized.LastIndexOf('.');

        if (comma >= 0 && dot >= 0)
        {
            char decimalSeparator = comma > dot ? ',' : '.';
            char thousandsSeparator = decimalSeparator == ',' ? '.' : ',';
            normalized = normalized.Replace(thousandsSeparator.ToString(), "", StringComparison.Ordinal);
            normalized = normalized.Replace(decimalSeparator, '.');
        }
        else if (comma >= 0)
        {
            normalized = normalized.Replace(',', '.');
        }

        return decimal.TryParse(
            normalized,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out parsed);
    }

    [GeneratedRegex(@"\r\n|\n|\r")]
    private static partial Regex SplitLinesRegex();
}

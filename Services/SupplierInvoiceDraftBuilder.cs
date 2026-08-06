using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public sealed class SupplierInvoiceDraftBuilder
{
    private const decimal TotalTolerance = 0.02m;

    private static readonly IReadOnlyDictionary<string, int> SpanishMonths =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["enero"] = 1,
            ["febrero"] = 2,
            ["marzo"] = 3,
            ["abril"] = 4,
            ["mayo"] = 5,
            ["junio"] = 6,
            ["julio"] = 7,
            ["agosto"] = 8,
            ["septiembre"] = 9,
            ["setiembre"] = 9,
            ["octubre"] = 10,
            ["noviembre"] = 11,
            ["diciembre"] = 12
        };

    public SupplierInvoiceDraft Build(DocumentReadResult document, int? commercialProjectId = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        string text = document.ExtractedText ?? "";
        string[] lines = GetLines(text);
        int recipientBoundary = FindRecipientBoundary(lines);
        string[] issuerLines = recipientBoundary > 0 ? lines[..recipientBoundary] : lines;
        string[] recipientLines = recipientBoundary >= 0 ? lines[recipientBoundary..Math.Min(lines.Length, recipientBoundary + 12)] : [];
        string providerName = ReadLabeledValue(issuerLines, "Proveedor", "Supplier", "Emisor") ??
                              FindIssuerCompanyName(issuerLines) ??
                              "";
        string? taxId = ReadTaxIdentifier(issuerLines);
        string invoiceNumber = ReadLabeledValue(
            lines,
            "Número de factura",
            "Numero de factura",
            "Invoice number",
            "Invoice no",
            "Factura") ?? "";
        DateTime invoiceDate = TryParseDate(ReadLabeledValue(
            lines,
            "Fecha de emisión",
            "Fecha factura",
            "Fecha de factura",
            "Fecha de emision",
            "Issue date",
            "Invoice date",
            "Fecha"));
        DateTime? dueDate = TryParseNullableDate(ReadLabeledValue(
            lines,
            "Fecha de vencimiento",
            "Due date"));
        string? currencyCode = DetectCurrency(text);
        var items = ParseItems(lines, commercialProjectId ?? 0);
        (decimal? taxBase, decimal? taxAmount, decimal? taxTotal) = ReadRetailTaxSummary(lines);
        string? explicitDescription = ReadLabeledValue(
            lines,
            "Descripción general",
            "Descripcion general",
            "Main description",
            "Descripción",
            "Descripcion",
            "Description");
        if (explicitDescription is not null &&
            NormalizeText(explicitDescription).StartsWith("CANTIDAD", StringComparison.Ordinal))
        {
            explicitDescription = null;
        }

        string mainDescription = explicitDescription ??
            items.FirstOrDefault()?.Description ??
            "";

        var draft = new SupplierInvoiceDraft
        {
            SupplierTaxId = taxId,
            ProviderName = providerName,
            IssuerCandidateBlock = string.Join(" | ", issuerLines.Where(line => !string.IsNullOrWhiteSpace(line)).Take(12)),
            RecipientCandidateBlock = string.Join(" | ", recipientLines.Where(line => !string.IsNullOrWhiteSpace(line))),
            IssuerSelectionReason = providerName.Length > 0
                ? (ReadLabeledValue(issuerLines, "Proveedor", "Supplier", "Emisor") is not null
                    ? "Etiqueta de emisor en la cabecera superior."
                    : "Razón social seleccionada en el bloque superior anterior al cliente.")
                : "No se encontró un emisor inequívoco en la cabecera.",
            InvoiceNumber = invoiceNumber,
            InvoiceDate = invoiceDate,
            DueDate = dueDate,
            MainDescription = mainDescription,
            CurrencyCode = currencyCode,
            PaymentNotes = ReadLabeledValue(lines, "Notas de pago", "Payment notes"),
            DocumentSubtotal = taxBase ?? ReadAmount(lines, "Subtotal", "Total sin impuestos", "Base imponible"),
            DocumentTaxTotal = taxAmount ?? ReadAmount(lines, "IVA total", "Tax amount", "Tax total", "Impuestos"),
            DocumentTotal = taxTotal ?? ReadAmount(lines, "Importe adeudado", "Amount due", "Total impuestos incluidos", "Total", "Invoice total"),
            CommercialProjectId = commercialProjectId,
            Items = items
        };

        if (string.IsNullOrWhiteSpace(draft.ProviderName) && string.IsNullOrWhiteSpace(draft.SupplierTaxId))
        {
            draft.Warnings.Add("El proveedor parece estar representado únicamente como imagen o logotipo; use --provider-id o será necesario OCR.");
        }

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
            draft.ValidationErrors.Add(
                draft.CommercialProjectValidationError ??
                $"El proyecto comercial con ID {draft.CommercialProjectId?.ToString() ?? "no indicado"} no es válido.");
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

            if (item.DocumentLineNetAmount.HasValue &&
                Math.Abs(item.DocumentLineNetAmount.Value - item.CalculatedNetAmount) > TotalTolerance)
            {
                draft.ValidationErrors.Add(
                    $"{prefix} la base calculada ({item.CalculatedNetAmount:F2}) no coincide con el PDF ({item.DocumentLineNetAmount.Value:F2}).");
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

    private static List<SupplierInvoiceItemDraft> ParseItems(string[] lines, int commercialProjectId)
    {
        var structured = ParseDelimitedItems(lines, commercialProjectId);
        if (structured.Count > 0)
        {
            return structured;
        }

        var labeled = ParseLabeledItem(lines, commercialProjectId);
        if (labeled is not null)
        {
            return [labeled];
        }

        var tabular = ParseTabularItems(lines, commercialProjectId);
        return tabular;
    }

    private static List<SupplierInvoiceItemDraft> ParseDelimitedItems(
        string[] lines,
        int commercialProjectId)
    {
        var items = new List<SupplierInvoiceItemDraft>();
        foreach (string line in lines)
        {
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

            items.Add(new SupplierInvoiceItemDraft
            {
                Position = items.Count + 1,
                Description = parts[1].Trim(),
                Amount = amount,
                UnitPrice = unitPrice,
                IVA = iva,
                DiscountPercent = parts.Length > 5 ? TryParseNullableDecimal(parts[5]) : null,
                CommercialProjectId = commercialProjectId
            });
        }

        return items;
    }

    private static SupplierInvoiceItemDraft? ParseLabeledItem(
        string[] lines,
        int commercialProjectId)
    {
        decimal? amount = ReadAmount(lines, "Cantidad", "Quantity");
        decimal? unitPrice = ReadAmount(lines, "Precio unitario", "Unit price");
        decimal? iva = ReadAmount(lines, "IVA", "Impuesto", "Tax rate");

        if (!amount.HasValue || !unitPrice.HasValue || !iva.HasValue)
        {
            return null;
        }

        string description = ReadLabeledValue(
            lines,
            "Descripción del item",
            "Descripcion del item",
            "Descripción",
            "Descripcion",
            "Producto",
            "Concepto") ?? FindDescriptionNearPeriodOrQuantity(lines) ?? "";
        string? period = ReadLabeledValue(lines, "Periodo", "Period");
        if (!string.IsNullOrWhiteSpace(period) &&
            !description.Contains(period, StringComparison.OrdinalIgnoreCase))
        {
            description = $"{description} - {period}".Trim(' ', '-');
        }

        return new SupplierInvoiceItemDraft
        {
            Position = 1,
            Description = description,
            Amount = amount.Value,
            UnitPrice = unitPrice.Value,
            IVA = iva.Value,
            DiscountPercent = ReadAmount(lines, "Descuento", "Discount"),
            DocumentLineNetAmount = ReadAmount(lines, "Importe base", "Line amount", "Base"),
            CommercialProjectId = commercialProjectId
        };
    }

    private static List<SupplierInvoiceItemDraft> ParseTabularItems(
        string[] lines,
        int commercialProjectId)
    {
        var items = new List<SupplierInvoiceItemDraft>();
        var retailRowPattern = new Regex(
            @"^(?<ean>\d{8,14})\s+(?<description>.+?)\s+(?<amount>\d[\d.,]*)\s+(?<price>\d[\d.,]*)\s+(?<discount>\d[\d.,]*)\s+(?<total>\d[\d.,]*)\s+(?<base>\d[\d.,]*)\s+(?<iva>\d[\d.,]*)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var rowPattern = new Regex(
            @"^(?<description>.*?)\s*(?<amount>\d+(?:[.,]\d+)?)\s+(?<price>\d[\d.,]*)\s*(?:US\$|USD|\$|EUR|€|GBP|£)?\s+(?<iva>\d+(?:[.,]\d+)?)\s*%\s+(?<base>\d[\d.,]*)\s*(?:US\$|USD|\$|EUR|€|GBP|£)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        for (int index = 0; index < lines.Length; index++)
        {
            Match retail = retailRowPattern.Match(lines[index]);
            if (retail.Success &&
                TryParseDecimal(retail.Groups["amount"].Value, out decimal retailAmount) &&
                TryParseDecimal(retail.Groups["price"].Value, out decimal retailPrice) &&
                TryParseDecimal(retail.Groups["discount"].Value, out decimal retailDiscount) &&
                TryParseDecimal(retail.Groups["total"].Value, out decimal retailTotal) &&
                TryParseDecimal(retail.Groups["base"].Value, out decimal retailBase) &&
                TryParseDecimal(retail.Groups["iva"].Value, out decimal retailIva))
            {
                items.Add(new SupplierInvoiceItemDraft
                {
                    Position = items.Count + 1,
                    Description = retail.Groups["description"].Value.Trim(),
                    Amount = retailAmount,
                    UnitPrice = retailPrice,
                    DiscountPercent = retailDiscount,
                    DocumentLineTotal = retailTotal,
                    DocumentLineNetAmount = retailBase,
                    IVA = retailIva,
                    CommercialProjectId = commercialProjectId
                });
                continue;
            }
            Match match = rowPattern.Match(lines[index]);
            if (!match.Success ||
                !TryParseDecimal(match.Groups["amount"].Value, out decimal amount) ||
                !TryParseDecimal(match.Groups["price"].Value, out decimal price) ||
                !TryParseDecimal(match.Groups["iva"].Value, out decimal iva) ||
                !TryParseDecimal(match.Groups["base"].Value, out decimal lineBase))
            {
                continue;
            }

            string description = match.Groups["description"].Value.Trim();
            if (string.IsNullOrWhiteSpace(description))
            {
                description = FindPreviousDescription(lines, index) ?? "";
            }

            string? period = FindAdjacentPeriod(lines, index);
            if (!string.IsNullOrWhiteSpace(period))
            {
                description = $"{description} - {period}".Trim(' ', '-');
            }

            items.Add(new SupplierInvoiceItemDraft
            {
                Position = items.Count + 1,
                Description = description,
                Amount = amount,
                UnitPrice = price,
                IVA = iva,
                DocumentLineNetAmount = lineBase,
                CommercialProjectId = commercialProjectId
            });
        }

        return items;
    }

    private static (decimal? Base, decimal? Tax, decimal? Total) ReadRetailTaxSummary(string[] lines)
    {
        foreach (string line in lines)
        {
            if (!NormalizeText(line).StartsWith("IVA ", StringComparison.Ordinal)) continue;
            string[] values = Regex.Matches(line, @"\d[\d.,]*")
                .Select(match => match.Value)
                .ToArray();
            if (values.Length >= 6 &&
                TryParseDecimal(values[1], out decimal basis) &&
                TryParseDecimal(values[2], out decimal tax) &&
                TryParseDecimal(values[^1], out decimal total))
            {
                return (basis, tax, total);
            }
        }
        return (null, null, null);
    }

    private static string[] GetIssuerBlock(string[] lines)
    {
        int boundary = Array.FindIndex(lines, line =>
        {
            string normalized = NormalizeText(line).TrimEnd(':');
            return normalized.StartsWith("FACTURAR A", StringComparison.Ordinal) ||
                   normalized.StartsWith("BILL TO", StringComparison.Ordinal) ||
                   normalized.StartsWith("FACTURADO A", StringComparison.Ordinal);
        });
        return boundary > 0 ? lines[..boundary] : lines;
    }

    private static int FindRecipientBoundary(string[] lines) => Array.FindIndex(lines, line =>
    {
        string normalized = NormalizeText(line).TrimEnd(':');
        return normalized is "CLIENTE" or "RECEPTOR" or "DESTINATARIO" ||
               normalized.StartsWith("FACTURAR A", StringComparison.Ordinal) ||
               normalized.StartsWith("FACTURADO A", StringComparison.Ordinal) ||
               normalized.StartsWith("BILL TO", StringComparison.Ordinal) ||
               normalized.StartsWith("NOMBRE", StringComparison.Ordinal) ||
               normalized.StartsWith("CIF DEL CLIENTE", StringComparison.Ordinal);
    });

    private static string? FindIssuerCompanyName(string[] issuerLines)
    {
        var suffixPattern = new Regex(
            @"^(?<company>.+?\b(?:LLC|L\.?L\.?C\.?|LTD\.?|LIMITED|INC\.?|CORP(?:ORATION)?\.?|S\.?L\.?|S\.?A\.?|GMBH|B\.?V\.?|SARL)\b[.,]?)(?:\s+\d.*)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (string line in issuerLines.Select(line => line.Trim()))
        {
            Match match = suffixPattern.Match(line);
            if (line.Length is > 2 and <= 500 &&
                match.Success &&
                !NormalizeText(line).StartsWith("FACTURA", StringComparison.Ordinal) &&
                !NormalizeText(line).StartsWith("NOMBRE", StringComparison.Ordinal) &&
                !NormalizeText(line).StartsWith("CIF", StringComparison.Ordinal))
            {
                return match.Groups["company"].Value.Trim();
            }
        }

        return null;
    }

    private static string? ReadTaxIdentifier(string[] lines)
    {
        var pattern = new Regex(
            @"\b(?:EU\s+OSS\s+VAT|VAT\s+ID|VAT|CIF|NIF)\b\s*(?:ID)?\s*[:#-]?\s*(?<value>[A-Z]{2}[A-Z0-9-]{5,}|[A-Z0-9][A-Z0-9-]{6,})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        for (int index = 0; index < lines.Length; index++)
        {
            Match match = pattern.Match(lines[index]);
            if (match.Success)
            {
                return match.Groups["value"].Value.Trim();
            }

            string normalized = NormalizeText(lines[index]);
            if (normalized is "VAT" or "VAT ID" or "EU OSS VAT" or "CIF" or "NIF")
            {
                string? next = NextValue(lines, index);
                if (next is not null && Regex.IsMatch(next, @"^[A-Z0-9-]{7,}$", RegexOptions.IgnoreCase))
                {
                    return next;
                }
            }
        }

        return null;
    }

    private static string? ReadLabeledValue(string[] lines, params string[] labels)
    {
        foreach (string label in labels)
        {
            var inline = new Regex(
                $@"^\s*{Regex.Escape(label)}\s*(?::|#|-)\s*(?<value>.+?)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            for (int index = 0; index < lines.Length; index++)
            {
                Match match = inline.Match(lines[index]);
                if (match.Success)
                {
                    return match.Groups["value"].Value.Trim();
                }

                if (NormalizeText(lines[index]) == NormalizeText(label))
                {
                    return NextValue(lines, index);
                }

                var spaceSeparated = Regex.Match(
                    lines[index],
                    $@"^\s*{Regex.Escape(label)}\s+(?<value>\S.+?)\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (spaceSeparated.Success)
                {
                    return spaceSeparated.Groups["value"].Value.Trim();
                }
            }
        }

        return null;
    }

    private static decimal? ReadAmount(string[] lines, params string[] labels)
    {
        foreach (string label in labels)
        {
            var inline = new Regex(
                $@"^\s*{Regex.Escape(label)}\s*(?::|#|-)?\s*(?<value>[-+]?\d[\d\s.,]*(?:\s*(?:US\$|USD|\$|EUR|€|GBP|£|%))?)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            for (int index = 0; index < lines.Length; index++)
            {
                Match match = inline.Match(lines[index]);
                if (match.Success && TryParseDecimal(match.Groups["value"].Value, out decimal value))
                {
                    return value;
                }

                if (NormalizeText(lines[index]) == NormalizeText(label) &&
                    TryParseDecimal(NextValue(lines, index), out value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? DetectCurrency(string text)
    {
        if (Regex.IsMatch(text, @"US\$|\bUSD\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "USD";
        }

        if (Regex.IsMatch(text, @"€|\bEUR\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "EUR";
        }

        if (Regex.IsMatch(text, @"£|\bGBP\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "GBP";
        }

        return Regex.IsMatch(text, @"\$", RegexOptions.CultureInvariant) ? "USD" : null;
    }

    private static DateTime TryParseDate(string? value) =>
        TryParseNullableDate(value) ?? DateTime.MinValue;

    private static DateTime? TryParseNullableDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Match spanish = Regex.Match(
            value,
            @"(?<day>\d{1,2})\s+de\s+(?<month>[\p{L}]+)\s+de\s+(?<year>\d{4})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (spanish.Success &&
            int.TryParse(spanish.Groups["day"].Value, out int day) &&
            int.TryParse(spanish.Groups["year"].Value, out int year) &&
            SpanishMonths.TryGetValue(spanish.Groups["month"].Value, out int month))
        {
            return new DateTime(year, month, day);
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

    private static string? FindDescriptionNearPeriodOrQuantity(string[] lines)
    {
        int marker = Array.FindIndex(lines, line =>
            StartsWithLabel(line, "Periodo") || StartsWithLabel(line, "Period"));
        if (marker < 0)
        {
            marker = Array.FindIndex(lines, line => StartsWithLabel(line, "Cantidad"));
        }

        return marker > 0 ? FindPreviousDescription(lines, marker) : null;
    }

    private static string? FindPreviousDescription(string[] lines, int index)
    {
        for (int current = index - 1; current >= 0 && current >= index - 4; current--)
        {
            string candidate = lines[current].Trim();
            string normalized = NormalizeText(candidate);
            if (candidate.Length > 2 &&
                !Regex.IsMatch(candidate, @"^\d") &&
                !normalized.Contains("DESCRIPCION CANTIDAD", StringComparison.Ordinal) &&
                !StartsWithAnyKnownLabel(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindAdjacentPeriod(string[] lines, int rowIndex)
    {
        for (int index = Math.Max(0, rowIndex - 2); index < Math.Min(lines.Length, rowIndex + 2); index++)
        {
            string? period = ReadLabeledValue([lines[index]], "Periodo", "Period");
            if (!string.IsNullOrWhiteSpace(period))
            {
                return period;
            }

            if (Regex.IsMatch(
                    lines[index],
                    @"^\d{1,2}\s+(?:de\s+)?[\p{L}]{3,}.*\d{4}",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return lines[index].Trim();
            }
        }

        return null;
    }

    private static bool StartsWithAnyKnownLabel(string line) =>
        new[]
        {
            "Proveedor", "Supplier", "VAT", "CIF", "NIF", "Factura", "Fecha", "Periodo",
            "Cantidad", "Precio", "IVA", "Importe", "Subtotal", "Total", "Moneda", "Currency"
        }.Any(label => StartsWithLabel(line, label));

    private static bool StartsWithLabel(string line, string label) =>
        NormalizeText(line).StartsWith(NormalizeText(label), StringComparison.Ordinal);

    private static string? NextValue(string[] lines, int index)
    {
        for (int current = index + 1; current < lines.Length; current++)
        {
            if (!string.IsNullOrWhiteSpace(lines[current]))
            {
                return lines[current].Trim();
            }
        }

        return null;
    }

    private static string[] GetLines(string text) =>
        Regex.Split(text, @"\r\n|\n|\r")
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim())
            .Where(line => line.Length > 0)
            .ToArray();

    private static string NormalizeText(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                result.Append(char.ToUpperInvariant(character));
            }
        }

        return Regex.Replace(result.ToString(), @"\s+", " ").Trim();
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

        string normalized = Regex.Replace(value.Trim(), @"[^0-9,\.\-+]", "");
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
}

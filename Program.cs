using InsertFacturasSQL.Configuration;
using InsertFacturasSQL.Models;
using InsertFacturasSQL.Services;
using Microsoft.Data.SqlClient;

namespace InsertFacturasSQL;

public static class Program
{
    private const int FailureExitCode = 1;
    private const int InvalidInvoiceExitCode = 2;

    public static int Main(string[] args)
    {
        if (args.Length == 1 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return ReadPdf(args[0]);
        }

        bool showRawText = args.Contains("--show-raw-text", StringComparer.OrdinalIgnoreCase);
        bool previewSql = args.Contains("--preview-sql", StringComparer.OrdinalIgnoreCase);
        string? requestedPdf = ReadPdfPath(args);

        if (showRawText && !previewSql)
        {
            return ShowRawText(requestedPdf);
        }

        if (!TryReadPreviewOptions(args, out string? pdfPath, out int? providerId, out int? currencyId, out int? projectId, out string? optionError))
        {
            Console.WriteLine(optionError);
            ShowUsage();
            return FailureExitCode;
        }

        return RunSqlPreview(pdfPath!, providerId, currencyId, projectId, showRawText);
    }

    private static int RunSqlPreview(string pdfPath, int? providerId, int? currencyId, int? projectId, bool showRawText)
    {
        Console.WriteLine(SupplierInvoiceSqlPreviewGenerator.PreviewWarning);

        var document = new PdfDocumentReader().Read(pdfPath);
        if (!document.IsSuccess)
        {
            Console.WriteLine($"No se pudo leer el PDF: {document.ErrorMessage}");
            Console.WriteLine("Resultado: NO VÁLIDA");
            return InvalidInvoiceExitCode;
        }

        if (showRawText)
        {
            PrintRawText(document);
        }

        var settings = DatabaseSettings.FromEnvironment();
        if (settings is null)
        {
            Console.WriteLine(
                $"No se ha configurado la variable {DatabaseSettings.ConnectionStringEnvironmentVariable}.");
            Console.WriteLine("Resultado: NO VÁLIDA");
            return InvalidInvoiceExitCode;
        }

        var builder = new SupplierInvoiceDraftBuilder();
        SupplierInvoiceDraft draft = builder.Build(document, projectId);
        draft.ProviderConfirmedManually = providerId.HasValue;
        draft.CurrencyConfirmedManually = currencyId.HasValue;
        draft.ProjectConfirmedManually = projectId.HasValue;

        try
        {
            using var connection = new SqlConnection(settings.ConnectionString);
            connection.Open();

            IProviderResolver providerResolver = new ProviderResolver();
            ProviderResolution provider = providerId.HasValue
                ? new ProviderResolution(providerResolver.ResolveById(connection, providerId.Value), [], null)
                : providerResolver.Resolve(connection, draft.SupplierTaxId, draft.ProviderName, draft.ProviderName);

            if (provider.Match is not null)
            {
                draft.CompanyId = provider.Match.CompanyID;
                draft.ProviderName = provider.Match.CompanyName;
            }
            else if (!string.IsNullOrWhiteSpace(provider.Warning))
            {
                draft.Warnings.Add(provider.Warning);
            }

            if (providerId.HasValue && provider.Match is null)
                draft.Warnings.Add($"El proveedor confirmado manualmente con CompanyID {providerId.Value} no existe en dbo.Companies.");

            foreach (ProviderMatch candidate in provider.Candidates)
            {
                if (provider.Match?.CompanyID != candidate.CompanyID)
                {
                    draft.ProviderCandidates.Add($"{candidate.CompanyID} - {candidate.CompanyName}");
                }
            }

            if (!string.IsNullOrWhiteSpace(provider.Warning) && provider.Match is not null)
            {
                draft.Warnings.Add(provider.Warning);
            }

            ICommercialProjectReader projectReader = new CommercialProjectReader();
            CommercialProjectMatch? project = projectId.HasValue ? projectReader.FindById(connection, projectId.Value) : null;
            if (project is not null)
            {
                draft.CommercialProjectId = project.CommercialProjectsID;
                draft.CommercialProjectName = project.Name;
                draft.CommercialProjectIsValid = project.IsValidForNewInvoice;
                draft.CommercialProjectValidationError = project.IsDisabled
                    ? $"El proyecto comercial con ID {projectId} está deshabilitado."
                    : !project.IsOpen
                        ? $"El proyecto comercial con ID {projectId} no está disponible para nuevas facturas."
                        : null;
            }
            else
            {
                draft.CommercialProjectValidationError =
                    $"El proyecto comercial con ID {projectId?.ToString() ?? "no indicado"} no existe o no se ha confirmado.";
            }


            ICurrencyReader currencyReader = new CurrencyReader();
            CurrencyMatch? configuredCurrency;
            if (currencyId.HasValue) draft.CurrencyId = currencyId.Value;
            configuredCurrency = currencyReader.FindById(connection, draft.CurrencyId);
            if (currencyId.HasValue && configuredCurrency is null)
                draft.Warnings.Add($"La moneda confirmada manualmente con CurrencyID {currencyId.Value} no existe.");
            if (!string.IsNullOrWhiteSpace(draft.CurrencyCode))
            {
                CurrencyMatch? detectedCurrency = currencyReader.FindByCode(connection, draft.CurrencyCode);
                draft.DetectedCurrencyId = detectedCurrency?.CurrencyID;

                if (detectedCurrency is null)
                {
                    draft.Warnings.Add(
                        $"La moneda {draft.CurrencyCode} del PDF no existe en dbo.Currency; no se ha asignado ningún ID detectado.");
                }
                else
                {
                    draft.CurrencyId = detectedCurrency.CurrencyID;
                    configuredCurrency = detectedCurrency;
                }
            }
            else
            {
                draft.Warnings.Add("No se detectó moneda en el PDF; se utiliza temporalmente CurrencyID 1.");
            }
            draft.ConfiguredCurrencyCode = configuredCurrency?.Code;

            if (draft.CompanyId.HasValue && draft.InvoiceDate >= new DateTime(1900, 1, 1))
            {
                draft.PotentialDuplicateCount = new SupplierInvoiceDuplicateChecker()
                    .CountPotentialDuplicates(connection, draft.CompanyId.Value, draft.InvoiceDate, draft.Notes);
            }
        }
        catch (SqlException)
        {
            draft.Warnings.Add("No se pudieron completar las consultas de validación en la base de datos.");
        }

        builder.Validate(draft);
        PrintDraft(draft);

        ISupplierInvoiceSqlPreviewGenerator generator = new SupplierInvoiceSqlPreviewGenerator();
        SupplierInvoiceSqlPreview preview = generator.Generate(draft);

        if (!preview.IsExecutablePreview)
        {
            Console.WriteLine();
            Console.WriteLine("Script SQL: no generado porque la factura no es válida.");
            Console.Write(SupplierInvoiceValidationReport.Build(draft, false).Text);
            Console.WriteLine("Resultado: NO VÁLIDA");
            Console.WriteLine(SupplierInvoiceSqlPreviewGenerator.PreviewWarning);
            return InvalidInvoiceExitCode;
        }

        Console.WriteLine();
        Console.WriteLine("SCRIPT SQL PARAMETRIZADO");
        Console.WriteLine(preview.CommandText);
        Console.WriteLine("PARÁMETROS");
        foreach (SqlPreviewParameter parameter in preview.Parameters)
        {
            Console.WriteLine(
                $"{parameter.Name} | {parameter.TypeDescription} | {parameter.DisplayValue}");
        }

        Console.Write(SupplierInvoiceValidationReport.Build(draft, true).Text);
        Console.WriteLine();
        Console.WriteLine("Resultado: LISTA PARA INSERTAR");
        Console.WriteLine(SupplierInvoiceSqlPreviewGenerator.PreviewWarning);
        return 0;
    }

    private static void PrintDraft(SupplierInvoiceDraft draft)
    {
        Console.WriteLine();
        Console.WriteLine("DATOS DETECTADOS");
        Console.WriteLine($"Proveedor {(draft.ProviderConfirmedManually ? "confirmado manualmente" : "detectado")}: {draft.ProviderName}");
        Console.WriteLine($"CIF/NIF/VAT: {draft.SupplierTaxId ?? "no detectado"}");
        Console.WriteLine($"Número de factura: {draft.InvoiceNumber}");
        Console.WriteLine($"Fecha: {(draft.InvoiceDate == DateTime.MinValue ? "no detectada" : draft.InvoiceDate.ToString("dd/MM/yyyy"))}");
        Console.WriteLine($"Fecha de vencimiento: {draft.DueDate?.ToString("dd/MM/yyyy") ?? "no detectada"}");
        Console.WriteLine($"Moneda detectada: {draft.CurrencyCode ?? "no detectada"} | CurrencyID {(draft.CurrencyConfirmedManually ? "confirmado manualmente" : "resuelto automáticamente")}: {draft.CurrencyId}");
        Console.WriteLine($"CurrencyID detectado: {draft.DetectedCurrencyId?.ToString() ?? "no resuelto"}");

        Console.WriteLine();
        Console.WriteLine("RESOLUCIONES");
        Console.WriteLine($"Proveedor: {draft.ProviderName} | CompanyID: {draft.CompanyId?.ToString() ?? "no resuelto"}");
        Console.WriteLine($"Bloque candidato a emisor: {draft.IssuerCandidateBlock}");
        Console.WriteLine($"Bloque candidato a receptor: {draft.RecipientCandidateBlock}");
        Console.WriteLine($"Motivo de selección del emisor: {draft.IssuerSelectionReason}");
        Console.WriteLine($"Project-id {(draft.ProjectConfirmedManually ? "confirmado manualmente" : "solicitado")}: {draft.CommercialProjectId?.ToString() ?? "no indicado"}");
        Console.WriteLine($"Proyecto: {draft.CommercialProjectName ?? "no resuelto"} | CommercialProjectsID: {draft.CommercialProjectId?.ToString() ?? "no resuelto"}");
        Console.WriteLine($"Notes: {draft.Notes}");
        Console.WriteLine($"InitDescription: {draft.InitDescription}");

        Console.WriteLine();
        Console.WriteLine("ITEMS");
        foreach (var item in draft.Items.OrderBy(item => item.Position))
        {
            Console.WriteLine(
                $"{item.Position}. {item.Description} | Cantidad {item.Amount} | Precio {item.UnitPrice:F2} | IVA {item.IVA}% | Base {item.CalculatedNetAmount:F2} | Total {item.CalculatedTotal:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("TOTALES CALCULADOS");
        Console.WriteLine($"Subtotal: {draft.CalculatedSubtotal:F2}");
        Console.WriteLine($"IVA: {draft.CalculatedTaxTotal:F2}");
        Console.WriteLine($"Total: {draft.CalculatedTotal:F2}");
        Console.WriteLine($"Subtotal documental: {draft.DocumentSubtotal?.ToString("F2") ?? "no detectado"}");
        Console.WriteLine($"IVA documental: {draft.DocumentTaxTotal?.ToString("F2") ?? "no detectado"}");
        Console.WriteLine($"Total documental: {draft.DocumentTotal?.ToString("F2") ?? "no detectado"}");

        Console.WriteLine();
        Console.WriteLine("ADVERTENCIAS");
        if (draft.Warnings.Count == 0)
        {
            Console.WriteLine("Ninguna.");
        }
        else
        {
            foreach (string warning in draft.Warnings.Distinct(StringComparer.Ordinal))
            {
                Console.WriteLine($"- {warning}");
            }
        }

        if (draft.ProviderCandidates.Count > 0)
        {
            Console.WriteLine("Candidatos de proveedor (no seleccionados automáticamente):");
            foreach (string candidate in draft.ProviderCandidates)
            {
                Console.WriteLine($"- {candidate}");
            }
        }

        if (draft.ValidationErrors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("ERRORES DE VALIDACIÓN");
            foreach (string error in draft.ValidationErrors)
            {
                Console.WriteLine($"- {error}");
            }
        }
    }

    private static bool TryReadPreviewOptions(
        string[] args,
        out string? pdfPath,
        out int? providerId,
        out int? currencyId,
        out int? projectId,
        out string? error)
    {
        pdfPath = ReadPdfPath(args);
        providerId = currencyId = projectId = null;
        error = null;

        bool previewSql = args.Contains("--preview-sql", StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(pdfPath) || !previewSql)
        {
            error = "Debe indicar un PDF y la opción --preview-sql.";
            return false;
        }

        if (!TryReadOptionalId(args, "--provider-id", out providerId, out error) ||
            !TryReadOptionalId(args, "--currency-id", out currencyId, out error) ||
            !TryReadOptionalId(args, "--project-id", out projectId, out error)) return false;

        if (!projectId.HasValue)
        {
            error = "--project-id debe contener un entero positivo.";
            return false;
        }

        return true;
    }

    private static bool TryReadOptionalId(string[] args, string option, out int? value, out string? error)
    {
        value = null;
        error = null;
        int index = Array.FindIndex(args, argument => string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return true;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int parsed) || parsed <= 0)
        {
            error = $"{option} debe contener un entero positivo.";
            return false;
        }
        value = parsed;
        return true;
    }

    private static string? ReadPdfPath(string[] args) =>
        args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : null;

    private static int ShowRawText(string? pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
        {
            Console.WriteLine("Debe indicar la ruta del PDF antes de --show-raw-text.");
            return FailureExitCode;
        }

        var document = new PdfDocumentReader().Read(pdfPath);
        if (!document.IsSuccess)
        {
            Console.WriteLine($"No se pudo leer el PDF: {document.ErrorMessage}");
            return FailureExitCode;
        }

        PrintRawText(document);
        return 0;
    }

    private static void PrintRawText(DocumentReadResult document)
    {
        Console.WriteLine("DIAGNÓSTICO: TEXTO EXTRAÍDO DEL PDF");
        Console.WriteLine("ADVERTENCIA: el texto puede contener datos sensibles del documento.");
        Console.WriteLine("--- INICIO TEXTO EXTRAÍDO ---");
        Console.WriteLine(document.ExtractedText);
        Console.WriteLine("--- FIN TEXTO EXTRAÍDO ---");
    }

    private static int ReadPdf(string filePath)
    {
        var result = new PdfDocumentReader().Read(filePath);
        Console.WriteLine($"Archivo: {result.FileName}");

        if (!result.IsSuccess)
        {
            Console.WriteLine($"Error: {result.ErrorMessage}");
            return FailureExitCode;
        }

        Console.WriteLine($"Páginas: {result.PageCount}");
        Console.WriteLine("Lectura correcta. Use --project-id <id> --preview-sql para preparar la factura.");
        return 0;
    }

    private static void ShowUsage() =>
        Console.WriteLine(
            "Uso: dotnet run -- \"ruta-factura.pdf\" --project-id 123 --preview-sql [--provider-id 1] [--currency-id 2] [--show-raw-text]");
}

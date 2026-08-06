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

        if (!TryReadPreviewOptions(args, out string? pdfPath, out int projectId, out string? optionError))
        {
            Console.WriteLine(optionError);
            ShowUsage();
            return FailureExitCode;
        }

        return RunSqlPreview(pdfPath!, projectId);
    }

    private static int RunSqlPreview(string pdfPath, int projectId)
    {
        Console.WriteLine(SupplierInvoiceSqlPreviewGenerator.PreviewWarning);

        var document = new PdfDocumentReader().Read(pdfPath);
        if (!document.IsSuccess)
        {
            Console.WriteLine($"No se pudo leer el PDF: {document.ErrorMessage}");
            Console.WriteLine("Resultado: NO VÁLIDA");
            return InvalidInvoiceExitCode;
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

        try
        {
            using var connection = new SqlConnection(settings.ConnectionString);
            connection.Open();

            IProviderResolver providerResolver = new ProviderResolver();
            ProviderResolution provider = providerResolver.Resolve(
                connection,
                draft.SupplierTaxId,
                draft.ProviderName,
                draft.ProviderName);

            if (provider.Match is not null)
            {
                draft.CompanyId = provider.Match.CompanyID;
                draft.ProviderName = provider.Match.CompanyName;
            }
            else if (!string.IsNullOrWhiteSpace(provider.Warning))
            {
                draft.Warnings.Add(provider.Warning);
            }

            if (!string.IsNullOrWhiteSpace(provider.Warning) && provider.Match is not null)
            {
                draft.Warnings.Add(provider.Warning);
            }

            ICommercialProjectReader projectReader = new CommercialProjectReader();
            CommercialProjectMatch? project = projectReader.FindById(connection, projectId);
            if (project is not null)
            {
                draft.CommercialProjectId = project.CommercialProjectsID;
                draft.CommercialProjectName = project.Name;
                draft.CommercialProjectIsValid = project.IsValidForNewInvoice;
            }

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

        Console.WriteLine();
        Console.WriteLine("Resultado: LISTA PARA INSERTAR");
        Console.WriteLine(SupplierInvoiceSqlPreviewGenerator.PreviewWarning);
        return 0;
    }

    private static void PrintDraft(SupplierInvoiceDraft draft)
    {
        Console.WriteLine();
        Console.WriteLine("DATOS DETECTADOS");
        Console.WriteLine($"Proveedor detectado: {draft.ProviderName}");
        Console.WriteLine($"CIF/NIF/VAT: {MaskIdentifier(draft.SupplierTaxId)}");
        Console.WriteLine($"Número de factura: {draft.InvoiceNumber}");
        Console.WriteLine($"Fecha: {(draft.InvoiceDate == DateTime.MinValue ? "no detectada" : draft.InvoiceDate.ToString("yyyy-MM-dd"))}");
        Console.WriteLine($"Moneda detectada: {draft.CurrencyCode ?? "no detectada"}");

        Console.WriteLine();
        Console.WriteLine("RESOLUCIONES");
        Console.WriteLine($"Proveedor: {draft.ProviderName} | CompanyID: {draft.CompanyId?.ToString() ?? "no resuelto"}");
        Console.WriteLine($"Proyecto: {draft.CommercialProjectName ?? "no resuelto"} | CommercialProjectsID: {draft.CommercialProjectId?.ToString() ?? "no resuelto"}");
        Console.WriteLine($"Notes: {draft.Notes}");
        Console.WriteLine($"InitDescription: {draft.InitDescription}");

        Console.WriteLine();
        Console.WriteLine("ITEMS");
        foreach (var item in draft.Items.OrderBy(item => item.Position))
        {
            Console.WriteLine(
                $"{item.Position}. {item.Description} | Cantidad {item.Amount} | Precio {item.UnitPrice} | IVA {item.IVA}% | Total {item.CalculatedTotal:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("TOTALES CALCULADOS");
        Console.WriteLine($"Subtotal: {draft.CalculatedSubtotal:F2}");
        Console.WriteLine($"IVA: {draft.CalculatedTaxTotal:F2}");
        Console.WriteLine($"Total: {draft.CalculatedTotal:F2}");

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
        out int projectId,
        out string? error)
    {
        pdfPath = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
        projectId = 0;
        error = null;

        bool previewSql = args.Contains("--preview-sql", StringComparer.OrdinalIgnoreCase);
        int projectOption = Array.FindIndex(args, argument =>
            string.Equals(argument, "--project-id", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(pdfPath) || !previewSql)
        {
            error = "Debe indicar un PDF y la opción --preview-sql.";
            return false;
        }

        if (projectOption < 0 || projectOption + 1 >= args.Length ||
            !int.TryParse(args[projectOption + 1], out projectId) || projectId <= 0)
        {
            error = "--project-id debe contener un entero positivo.";
            return false;
        }

        return true;
    }

    private static string MaskIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "no detectado";
        }

        string compact = new(value.Where(char.IsLetterOrDigit).ToArray());
        return compact.Length <= 4
            ? new string('*', compact.Length)
            : $"{new string('*', compact.Length - 4)}{compact[^4..]}";
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
        Console.WriteLine("Uso: dotnet run -- \"ruta-factura.pdf\" --project-id 123 --preview-sql");
}

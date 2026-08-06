using System.Text.Json;
using InsertFacturasSQL.Configuration;
using InsertFacturasSQL.Models;
using InsertFacturasSQL.Services;

namespace InsertFacturasSQL;

public static class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Iniciando programa...");

        if (args.Length > 0)
        {
            ReadPdf(args[0]);
            return;
        }

        string folderPath = Path.Combine(Directory.GetCurrentDirectory(), "JSON");
        Console.WriteLine($"Buscando JSON en: {folderPath}");

        var databaseSettings = DatabaseSettings.FromEnvironment();
        if (databaseSettings is null)
        {
            Console.WriteLine(
                $"No se ha configurado la variable de entorno {DatabaseSettings.ConnectionStringEnvironmentVariable}.");
            return;
        }

        try
        {
            if (!Directory.Exists(folderPath))
            {
                Console.WriteLine("Carpeta no encontrada");
                return;
            }

            var files = Directory.GetFiles(folderPath, "*.json");
            if (files.Length == 0)
            {
                Console.WriteLine("No hay archivos JSON");
                return;
            }

            var validator = new InvoiceValidator();
            var providerResolver = new ProviderResolver();
            using var importer = new SqlInvoiceImporter(databaseSettings, providerResolver);

            importer.Open();
            Console.WriteLine("Conexion SQL correcta");

            foreach (var jsonPath in files)
            {
                Console.WriteLine("=================================");
                Console.WriteLine($"Procesando: {jsonPath}");

                try
                {
                    string json = File.ReadAllText(jsonPath);
                    var deserializedInvoice = JsonSerializer.Deserialize<Factura>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    var factura = validator.Validate(deserializedInvoice);

                    Console.WriteLine($"Proveedor: {factura.ProviderName}");

                    int orderId = importer.Import(factura);
                    Console.WriteLine($"Insert OK. ID = {orderId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error:");
                    Console.WriteLine(ex.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error general:");
            Console.WriteLine(ex.ToString());
        }

        Console.WriteLine("Fin del proceso");
        Console.ReadKey();
    }

    private static void ReadPdf(string filePath)
    {
        IDocumentReader documentReader = new PdfDocumentReader();
        var result = documentReader.Read(filePath);

        Console.WriteLine($"Archivo: {result.FileName}");
        Console.WriteLine($"Ruta: {result.FilePath}");

        if (!result.IsSuccess)
        {
            Console.WriteLine($"Error: {result.ErrorMessage}");
            return;
        }

        Console.WriteLine($"Páginas: {result.PageCount}");
        Console.WriteLine("Texto extraído:");
        Console.WriteLine(result.ExtractedText);
    }
}

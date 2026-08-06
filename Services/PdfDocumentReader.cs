using System.Text;
using InsertFacturasSQL.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace InsertFacturasSQL.Services;

public sealed class PdfDocumentReader : IDocumentReader
{
    public DocumentReadResult Read(string filePath)
    {
        string normalizedPath = "";
        string fileName = "";

        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return Failure(normalizedPath, fileName, "La ruta del archivo PDF es obligatoria.");
            }

            normalizedPath = Path.GetFullPath(filePath);
            fileName = Path.GetFileName(normalizedPath);

            if (!string.Equals(Path.GetExtension(normalizedPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(normalizedPath, fileName, "El archivo debe tener extensión .pdf.");
            }

            var fileInfo = new FileInfo(normalizedPath);
            if (!fileInfo.Exists)
            {
                return Failure(normalizedPath, fileName, "El archivo PDF no existe.");
            }

            if (fileInfo.Length == 0)
            {
                return Failure(normalizedPath, fileName, "El archivo PDF está vacío.");
            }

            using var document = PdfDocument.Open(normalizedPath);
            var text = new StringBuilder();

            foreach (var page in document.GetPages())
            {
                if (text.Length > 0)
                {
                    text.AppendLine();
                    text.AppendLine();
                }

                text.Append(ContentOrderTextExtractor.GetText(page));
            }

            return new DocumentReadResult
            {
                FilePath = normalizedPath,
                FileName = fileName,
                ExtractedText = text.ToString(),
                PageCount = document.NumberOfPages,
                IsSuccess = true
            };
        }
        catch (Exception ex)
        {
            return Failure(normalizedPath, fileName, $"No se pudo leer el archivo PDF: {ex.Message}");
        }
    }

    private static DocumentReadResult Failure(string filePath, string fileName, string errorMessage)
    {
        return new DocumentReadResult
        {
            FilePath = filePath,
            FileName = fileName,
            IsSuccess = false,
            ErrorMessage = errorMessage
        };
    }
}

using InsertFacturasSQL.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace InsertFacturasSQL.Tests;

public sealed class PdfDocumentReaderTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"InsertFacturasSQL.Tests-{Guid.NewGuid():N}");

    public PdfDocumentReaderTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void Read_WhenPdfIsValid_ReturnsTextAndPageCount()
    {
        string pdfPath = Path.Combine(_testDirectory, "factura.pdf");
        CreatePdf(pdfPath, "Factura digital", "Segunda pagina");
        var reader = new PdfDocumentReader();

        var result = reader.Read(pdfPath);

        Assert.True(result.IsSuccess);
        Assert.Equal(Path.GetFullPath(pdfPath), result.FilePath);
        Assert.Equal("factura.pdf", result.FileName);
        Assert.Equal(2, result.PageCount);
        Assert.Contains("Factura digital", result.ExtractedText);
        Assert.Contains("Segunda pagina", result.ExtractedText);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Read_WhenFileDoesNotExist_ReturnsFailure()
    {
        var reader = new PdfDocumentReader();

        var result = reader.Read(Path.Combine(_testDirectory, "inexistente.pdf"));

        Assert.False(result.IsSuccess);
        Assert.Contains("no existe", result.ErrorMessage);
    }

    [Fact]
    public void Read_WhenExtensionIsNotPdf_ReturnsFailure()
    {
        string filePath = Path.Combine(_testDirectory, "factura.txt");
        File.WriteAllText(filePath, "contenido");
        var reader = new PdfDocumentReader();

        var result = reader.Read(filePath);

        Assert.False(result.IsSuccess);
        Assert.Contains("extensión .pdf", result.ErrorMessage);
    }

    [Fact]
    public void Read_WhenPdfIsEmpty_ReturnsFailure()
    {
        string pdfPath = Path.Combine(_testDirectory, "vacio.pdf");
        File.WriteAllBytes(pdfPath, []);
        var reader = new PdfDocumentReader();

        var result = reader.Read(pdfPath);

        Assert.False(result.IsSuccess);
        Assert.Contains("vacío", result.ErrorMessage);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
    }

    private static void CreatePdf(string path, params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (string pageText in pageTexts)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(pageText, 12, new PdfPoint(25, 700), font);
        }

        File.WriteAllBytes(path, builder.Build());
    }
}

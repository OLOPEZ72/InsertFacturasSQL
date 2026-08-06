using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace InsertFacturasSQL.Tests;

public sealed class ProgramExitCodeTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"InsertFacturasSQL.ProgramTests-{Guid.NewGuid():N}");

    public ProgramExitCodeTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void Main_WhenPdfReadSucceeds_ReturnsZero()
    {
        string pdfPath = Path.Combine(_testDirectory, "valido.pdf");
        CreatePdf(pdfPath);

        int exitCode = Program.Main([pdfPath]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Main_WhenPdfReadFails_ReturnsNonZero()
    {
        string pdfPath = Path.Combine(_testDirectory, "vacio.pdf");
        File.WriteAllBytes(pdfPath, []);

        int exitCode = Program.Main([pdfPath]);

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Main_WithoutPreviewArguments_DoesNotStartLegacyDatabaseImport()
    {
        using var output = new StringWriter();
        TextWriter previous = Console.Out;
        Console.SetOut(output);

        try
        {
            int exitCode = Program.Main([]);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("--preview-sql", output.ToString());
            Assert.DoesNotContain("Insert OK", output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    [Fact]
    public void Main_WithShowRawText_PrintsExtractedTextWithoutDatabaseAccess()
    {
        string pdfPath = Path.Combine(_testDirectory, "diagnostico.pdf");
        CreatePdf(pdfPath, "Texto diagnostico seguro");
        using var output = new StringWriter();
        TextWriter previous = Console.Out;
        Console.SetOut(output);

        try
        {
            int exitCode = Program.Main([pdfPath, "--show-raw-text"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("TEXTO EXTRAÍDO", output.ToString());
            Assert.Contains("Texto diagnostico seguro", output.ToString());
            Assert.Contains("puede contener datos sensibles", output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    [Theory]
    [InlineData("--provider-id")]
    [InlineData("--currency-id")]
    [InlineData("--project-id")]
    public void Main_WithInvalidManualReferenceId_RejectsBeforeDatabaseAccess(string option)
    {
        using var output = new StringWriter();
        TextWriter previous = Console.Out;
        Console.SetOut(output);
        try
        {
            int exitCode = Program.Main(["invoice.pdf", "--preview-sql", option, "no-es-un-entero"]);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("debe contener un entero positivo", output.ToString());
            Assert.DoesNotContain("INSERT INTO", output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
    }

    private static void CreatePdf(string path, string text = "PDF de prueba")
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(text, 12, new PdfPoint(25, 700), font);
        File.WriteAllBytes(path, builder.Build());
    }
}

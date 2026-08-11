using System.Data;
using System.Text.RegularExpressions;
using InsertFacturasSQL.Models;
using InsertFacturasSQL.Services;

namespace InsertFacturasSQL.Tests;

public sealed class SupplierInvoiceSqlPreviewGeneratorTests
{
    private readonly SupplierInvoiceSqlPreviewGenerator _generator = new();

    [Fact]
    public void Generate_CreatesParameterizedHeaderAndIdentityRecovery()
    {
        var draft = CreateValidDraft();

        SupplierInvoiceSqlPreview preview = _generator.Generate(draft);

        Assert.True(preview.IsExecutablePreview);
        Assert.Contains("INSERT INTO dbo.ProviderOrder", preview.CommandText);
        Assert.Contains("@ProviderOrderDate", preview.CommandText);
        Assert.Contains("@Provider", preview.CommandText);
        Assert.Contains("CONVERT(int, SCOPE_IDENTITY())", preview.CommandText);
        Assert.Contains("SELECT @ProviderOrderID AS ProviderOrderID", preview.CommandText);
        Assert.Contains("BEGIN TRANSACTION", preview.CommandText);
        Assert.Contains("COMMIT TRANSACTION", preview.CommandText);
        Assert.Contains("ROLLBACK TRANSACTION", preview.CommandText);
        Assert.Contains("SELECT", preview.CommandText);
        Assert.Contains("@PersistedFinalTotal", preview.CommandText);
        Assert.Contains("ItemsProviderOrder", preview.CommandText);
        Assert.Equal(0.02m, preview.Parameters.Single(value => value.Name == "@InvoiceTotalTolerance").Value);
        Assert.DoesNotContain(draft.ProviderName, preview.CommandText);
        Assert.DoesNotContain(draft.InvoiceNumber, preview.CommandText);
    }

    [Fact]
    public void Generate_CreatesOneItemInsertPerItem()
    {
        var oneItem = CreateValidDraft();
        var multipleItems = CreateValidDraft(itemCount: 3);

        var onePreview = _generator.Generate(oneItem);
        var multiplePreview = _generator.Generate(multipleItems);

        Assert.Single(Regex.Matches(onePreview.CommandText, "INSERT INTO dbo.ItemsProviderOrder").Cast<Match>());
        Assert.Equal(3, Regex.Matches(multiplePreview.CommandText, "INSERT INTO dbo.ItemsProviderOrder").Count);
        Assert.Contains("@Item3Description", multiplePreview.CommandText);
    }

    [Fact]
    public void Generate_StoresQuotesAndNewLinesOnlyInParameterValues()
    {
        var draft = CreateValidDraft(description: "Servicio de O'Brien\r\nSegunda línea");

        SupplierInvoiceSqlPreview preview = _generator.Generate(draft);
        SqlPreviewParameter parameter = Assert.Single(
            preview.Parameters,
            value => value.Name == "@Item1Description");

        Assert.Equal("Servicio de O'Brien\r\nSegunda línea", parameter.Value);
        Assert.Contains("O'Brien", parameter.DisplayValue);
        Assert.Contains("\\r\\n", parameter.DisplayValue);
        Assert.DoesNotContain("O'Brien", preview.CommandText);
    }

    [Fact]
    public void Generate_RepresentsNullValuesWithoutEmbeddingThemInSql()
    {
        var draft = CreateValidDraft();

        SupplierInvoiceSqlPreview preview = _generator.Generate(draft);

        Assert.Null(preview.Parameters.Single(value => value.Name == "@PaymentNotes").Value);
        Assert.Null(preview.Parameters.Single(value => value.Name == "@PaymentDate").Value);
        Assert.Null(preview.Parameters.Single(value => value.Name == "@Item1Discount").Value);
        Assert.Equal("NULL", preview.Parameters.Single(value => value.Name == "@PaymentNotes").DisplayValue);
    }

    [Fact]
    public void Generate_PreservesDecimalPrecisionAndInvariantFormatting()
    {
        var draft = CreateValidDraft(amount: 2.25m, unitPrice: 1234.567m, iva: 21.00m);

        SupplierInvoiceSqlPreview preview = _generator.Generate(draft);
        var amount = preview.Parameters.Single(value => value.Name == "@Item1Amount");
        var unitPrice = preview.Parameters.Single(value => value.Name == "@Item1UnitPrice");

        Assert.Equal(SqlDbType.Decimal, amount.SqlType);
        Assert.Equal((byte)18, amount.Precision);
        Assert.Equal((byte)2, amount.Scale);
        Assert.Equal("2.25", amount.DisplayValue);
        Assert.Equal((byte)3, unitPrice.Scale);
        Assert.Equal("1234.567", unitPrice.DisplayValue);
    }

    [Fact]
    public void Generate_PreservesInvoiceDateAsSmallDateTimeParameter()
    {
        var draft = CreateValidDraft(invoiceDate: new DateTime(2026, 8, 6));

        SupplierInvoiceSqlPreview preview = _generator.Generate(draft);
        var date = preview.Parameters.Single(value => value.Name == "@ProviderOrderDate");

        Assert.Equal(SqlDbType.SmallDateTime, date.SqlType);
        Assert.Equal(new DateTime(2026, 8, 6), date.Value);
        Assert.Equal("2026-08-06 00:00:00", date.DisplayValue);
    }

    [Fact]
    public void Generate_DoesNotExposeConnectionStringOrRequireDatabaseAccess()
    {
        const string secret = "Server=private;Password=never-show-this";
        string? previous = Environment.GetEnvironmentVariable("INSERT_FACTURAS_SQL_CONNECTION_STRING");
        Environment.SetEnvironmentVariable("INSERT_FACTURAS_SQL_CONNECTION_STRING", secret);

        try
        {
            SupplierInvoiceSqlPreview preview = _generator.Generate(CreateValidDraft());

            Assert.True(preview.IsExecutablePreview);
            Assert.DoesNotContain(secret, preview.CommandText);
            Assert.DoesNotContain("never-show-this", string.Join('|', preview.Parameters.Select(value => value.DisplayValue)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INSERT_FACTURAS_SQL_CONNECTION_STRING", previous);
        }
    }

    [Fact]
    public void Generate_WhenInvoiceIsInvalid_DoesNotGenerateExecutableSql()
    {
        var draft = CreateValidDraft();
        draft.ValidationErrors.Add("Factura inválida para la prueba.");

        SupplierInvoiceSqlPreview preview = _generator.Generate(draft);

        Assert.False(preview.IsExecutablePreview);
        Assert.Empty(preview.CommandText);
        Assert.Empty(preview.Parameters);
        Assert.Contains("Factura inválida para la prueba.", preview.Errors);
    }

    private static SupplierInvoiceDraft CreateValidDraft(
        int itemCount = 1,
        string description = "Licencia anual",
        decimal amount = 1m,
        decimal unitPrice = 100m,
        decimal iva = 21m,
        DateTime? invoiceDate = null)
    {
        var draft = new SupplierInvoiceDraft
        {
            ProviderName = "Proveedor de prueba",
            InvoiceNumber = "TEST-2026-001",
            InvoiceDate = invoiceDate ?? new DateTime(2026, 8, 6),
            MainDescription = "Servicios de prueba",
            CompanyId = 123,
            CommercialProjectId = 456,
            CommercialProjectName = "Proyecto de prueba",
            CommercialProjectIsValid = true
        };

        for (int index = 1; index <= itemCount; index++)
        {
            draft.Items.Add(new SupplierInvoiceItemDraft
            {
                Position = index,
                Description = description,
                Amount = amount,
                UnitPrice = unitPrice,
                IVA = iva,
                CommercialProjectId = 456
            });
        }

        return draft;
    }
}

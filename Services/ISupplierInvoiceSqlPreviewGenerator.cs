using System.Data;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public interface ISupplierInvoiceSqlPreviewGenerator
{
    SupplierInvoiceSqlPreview Generate(SupplierInvoiceDraft draft);
}

public sealed record SqlPreviewParameter(
    string Name,
    object? Value,
    SqlDbType SqlType,
    int? Size = null,
    byte? Precision = null,
    byte? Scale = null)
{
    public string TypeDescription => SqlType switch
    {
        SqlDbType.NVarChar or SqlDbType.NChar when Size.HasValue => $"{SqlType.ToString().ToLowerInvariant()}({Size})",
        SqlDbType.Decimal when Precision.HasValue && Scale.HasValue => $"decimal({Precision},{Scale})",
        _ => SqlType.ToString().ToLowerInvariant()
    };

    public string DisplayValue => Value switch
    {
        null => "NULL",
        string text => JsonSerializer.Serialize(
            text,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        bool boolean => boolean ? "1" : "0",
        _ => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "NULL"
    };
}

public sealed record SupplierInvoiceSqlPreview(
    string CommandText,
    IReadOnlyList<SqlPreviewParameter> Parameters,
    IReadOnlyList<string> Errors)
{
    public bool IsExecutablePreview => Errors.Count == 0 && !string.IsNullOrWhiteSpace(CommandText);
}

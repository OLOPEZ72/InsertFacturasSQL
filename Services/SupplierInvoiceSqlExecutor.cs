using System.Data;
using Microsoft.Data.SqlClient;
using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

/// <summary>
/// Executes exactly the command produced by SupplierInvoiceSqlPreviewGenerator.
/// The command owns its transaction and performs the post-insert reconciliation.
/// </summary>
public sealed class SupplierInvoiceSqlExecutor : ISupplierInvoiceSqlExecutor
{
    private readonly ISupplierInvoiceSqlPreviewGenerator _previewGenerator;

    public SupplierInvoiceSqlExecutor(ISupplierInvoiceSqlPreviewGenerator? previewGenerator = null)
    {
        _previewGenerator = previewGenerator ?? new SupplierInvoiceSqlPreviewGenerator();
    }

    public int Execute(SqlConnection connection, SupplierInvoiceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(draft);

        SupplierInvoiceSqlPreview preview = _previewGenerator.Generate(draft);
        if (!preview.IsExecutablePreview)
            throw new InvalidOperationException(string.Join(" ", preview.Errors));

        using SqlCommand command = connection.CreateCommand();
        command.CommandText = preview.CommandText;
        command.CommandType = CommandType.Text;
        foreach (SqlPreviewParameter parameter in preview.Parameters)
        {
            SqlParameter sqlParameter = command.Parameters.Add(parameter.Name, parameter.SqlType);
            sqlParameter.Value = parameter.Value ?? DBNull.Value;
            if (parameter.Size.HasValue) sqlParameter.Size = parameter.Size.Value;
            if (parameter.Precision.HasValue) sqlParameter.Precision = parameter.Precision.Value;
            if (parameter.Scale.HasValue) sqlParameter.Scale = parameter.Scale.Value;
        }

        object? result = command.ExecuteScalar();
        if (result is null || result is DBNull || !int.TryParse(result.ToString(), out int providerOrderId))
            throw new InvalidOperationException("La transacción terminó sin devolver ProviderOrderID.");
        return providerOrderId;
    }
}

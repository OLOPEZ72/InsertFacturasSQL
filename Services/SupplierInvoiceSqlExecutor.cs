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

        var diagnostics = new List<string>();
        try
        {
            using SqlDataReader reader = command.ExecuteReader();
            int providerOrderId = 0;
            do
            {
                while (reader.Read())
                {
                    if (reader.HasColumn("CalculatedBase"))
                    {
                        diagnostics.Add(
                            $"ProviderOrderID={reader["ProviderOrderID"]}; Description={reader["Description"]}; Amount={reader["Amount"]}; UnitPrice={reader["UnitPrice"]}; Discount={reader["Discount"]}; IVA={reader["IVA"]}; CProjectID={reader["CProjectID"]}; Base={reader["CalculatedBase"]}; IVA calculado={reader["CalculatedTax"]}");
                    }
                    else if (reader.HasColumn("PersistedFinalTotal"))
                    {
                        diagnostics.Add(
                            $"Base total={reader["PersistedBaseTotal"]}; IVA total={reader["PersistedTaxTotal"]}; Total calculado={reader["PersistedFinalTotal"]}; Total esperado={reader["ExpectedInvoiceTotal"]}; Diferencia={reader["Difference"]}; Tolerancia={reader["Tolerance"]}");
                    }
                    else if (reader.HasColumn("ProviderOrderID") && reader.FieldCount == 1)
                    {
                        providerOrderId = Convert.ToInt32(reader["ProviderOrderID"]);
                    }
                }
            }
            while (reader.NextResult());

            if (providerOrderId <= 0)
                throw new InvalidOperationException("La transacción terminó sin devolver ProviderOrderID.");
            return providerOrderId;
        }
        catch (SqlException ex) when (diagnostics.Count > 0)
        {
            throw new SupplierInvoiceExecutionException(ex.Message, diagnostics, ex);
        }
    }
}

public sealed class SupplierInvoiceExecutionException : Exception
{
    public SupplierInvoiceExecutionException(string message, IReadOnlyList<string> diagnostics, Exception innerException)
        : base(message, innerException) => Diagnostics = diagnostics;

    public IReadOnlyList<string> Diagnostics { get; }
}

internal static class SqlDataReaderExtensions
{
    public static bool HasColumn(this SqlDataReader reader, string name)
    {
        for (int index = 0; index < reader.FieldCount; index++)
            if (string.Equals(reader.GetName(index), name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

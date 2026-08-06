using Microsoft.Data.SqlClient;

namespace InsertFacturasSQL.Services;

public sealed class SupplierInvoiceDuplicateChecker
{
    private const string SelectPotentialDuplicatesSql = """
SELECT TOP (10) ProviderOrderID
FROM dbo.ProviderOrder
WHERE Provider = @Provider
  AND ProviderOrderDate >= @InvoiceDateFrom
  AND ProviderOrderDate < @InvoiceDateTo
  AND Notes = @Notes
  AND (Disabled IS NULL OR Disabled = 0)
ORDER BY ProviderOrderID DESC;
""";

    public int CountPotentialDuplicates(
        SqlConnection connection,
        int companyId,
        DateTime invoiceDate,
        string notes)
    {
        using var command = new SqlCommand(SelectPotentialDuplicatesSql, connection);
        command.Parameters.Add("@Provider", System.Data.SqlDbType.Int).Value = companyId;
        command.Parameters.Add("@InvoiceDateFrom", System.Data.SqlDbType.SmallDateTime).Value = invoiceDate.Date;
        command.Parameters.Add("@InvoiceDateTo", System.Data.SqlDbType.SmallDateTime).Value = invoiceDate.Date.AddDays(1);
        command.Parameters.Add("@Notes", System.Data.SqlDbType.NVarChar, 250).Value = notes;

        int count = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            count++;
        }

        return count;
    }
}

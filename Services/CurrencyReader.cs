using System.Data;
using Microsoft.Data.SqlClient;

namespace InsertFacturasSQL.Services;

public sealed class CurrencyReader : ICurrencyReader
{
    private const string SelectByCodeSql = """
SELECT TOP (10) CurrencyID, Code, Name, Symbol
FROM dbo.Currency
WHERE UPPER(LTRIM(RTRIM(Code))) = @Code
ORDER BY CurrencyID;
""";

    private const string SelectByIdSql = """
SELECT TOP (1) CurrencyID, Code, Name, Symbol
FROM dbo.Currency
WHERE CurrencyID = @CurrencyID;
""";

    public CurrencyMatch? FindByCode(SqlConnection connection, string code)
    {
        using var command = new SqlCommand(SelectByCodeSql, connection);
        command.Parameters.Add("@Code", SqlDbType.VarChar, 50).Value = code.Trim().ToUpperInvariant();
        return ReadSingle(command);
    }

    public CurrencyMatch? FindById(SqlConnection connection, int currencyId)
    {
        using var command = new SqlCommand(SelectByIdSql, connection);
        command.Parameters.Add("@CurrencyID", SqlDbType.Int).Value = currencyId;
        return ReadSingle(command);
    }

    private static CurrencyMatch? ReadSingle(SqlCommand command)
    {
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new CurrencyMatch(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? "" : reader.GetString(1).Trim(),
            reader.IsDBNull(2) ? "" : reader.GetString(2).Trim(),
            reader.IsDBNull(3) ? "" : reader.GetString(3).Trim());
    }
}

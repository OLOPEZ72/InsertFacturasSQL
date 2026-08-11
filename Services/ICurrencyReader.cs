using Microsoft.Data.SqlClient;

namespace InsertFacturasSQL.Services;

public interface ICurrencyReader
{
    CurrencyMatch? FindByCode(SqlConnection connection, string code);
    CurrencyMatch? FindById(SqlConnection connection, int currencyId);
}

public sealed record CurrencyMatch(int CurrencyID, string Code, string Name, string Symbol);

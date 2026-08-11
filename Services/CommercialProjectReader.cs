using Microsoft.Data.SqlClient;

namespace InsertFacturasSQL.Services;

public sealed class CommercialProjectReader : ICommercialProjectReader
{
    private const string SelectProjectSql = """
SELECT TOP (1)
    CommercialProjectsID,
    Name,
    [Open],
    Disabled
FROM dbo.CommercialProjects
WHERE CommercialProjectsID = @CommercialProjectsID;
""";

    public CommercialProjectMatch? FindById(SqlConnection connection, int commercialProjectId)
    {
        using var command = new SqlCommand(SelectProjectSql, connection);
        command.Parameters.Add("@CommercialProjectsID", System.Data.SqlDbType.Int).Value = commercialProjectId;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new CommercialProjectMatch(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? "" : reader.GetString(1),
            !reader.IsDBNull(2) && reader.GetBoolean(2),
            !reader.IsDBNull(3) && reader.GetBoolean(3));
    }
}

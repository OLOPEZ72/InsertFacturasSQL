using Microsoft.Data.SqlClient;

namespace InsertFacturasSQL.Services;

public interface ICommercialProjectReader
{
    CommercialProjectMatch? FindById(SqlConnection connection, int commercialProjectId);
}

public sealed record CommercialProjectMatch(
    int CommercialProjectsID,
    string Name,
    bool IsOpen,
    bool IsDisabled)
{
    public bool IsValidForNewInvoice => IsOpen && !IsDisabled;
}

using Microsoft.Data.SqlClient;

namespace InsertFacturasSQL.Services;

public interface IProviderResolver
{
    ProviderResolution Resolve(
        SqlConnection connection,
        string? taxId,
        string? legalName,
        string? tradeName);

    ProviderMatch? ResolveById(SqlConnection connection, int companyId);
}

public sealed record ProviderResolution(
    ProviderMatch? Match,
    IReadOnlyList<ProviderMatch> Candidates,
    string? Warning,
    ProviderResolutionDiagnostic? Diagnostic = null)
{
    public bool IsAmbiguous => Match is null && Candidates.Count > 1;
}

using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace InsertFacturasSQL.Services;

public sealed class ProviderResolver : IProviderResolver
{
    private const string SelectByIdSql = """
SELECT TOP (1) CompanyID, Name FROM dbo.Companies WHERE CompanyID = @CompanyID;
""";
    private const string SelectByTaxIdSql = """
SELECT TOP (10) CompanyID, Name FROM dbo.Companies
WHERE Provider = 1 AND Disabled IS NULL
  AND UPPER(REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(CIF)), ' ', ''), '-', ''), '.', ''), '/', '')) = @TaxId
ORDER BY CompanyID;
""";
    private const string SelectByNameSql = """
SELECT TOP (10) CompanyID, Name FROM dbo.Companies
WHERE Provider = 1 AND Disabled IS NULL AND Name LIKE @NamePattern ESCAPE '\'
ORDER BY CompanyID;
""";

    public ProviderResolution Resolve(SqlConnection connection, string? taxId, string? legalName, string? tradeName)
    {
        string normalizedTaxId = NormalizeIdentifier(taxId);
        bool taxWasProvided = normalizedTaxId.Length > 0;
        if (taxWasProvided)
        {
            List<ProviderMatch> taxMatches = ReadMatches(connection, SelectByTaxIdSql, "@TaxId", normalizedTaxId);
            if (taxMatches.Count == 1)
            {
                ProviderMatch match = taxMatches[0] with { MatchType = "TaxIdExact" };
                return new ProviderResolution(match, taxMatches, null,
                    new ProviderResolutionDiagnostic(taxId ?? "", normalizedTaxId, match.CompanyName, "VAT/CIF/NIF exacto normalizado", "Muy alta"));
            }
            if (taxMatches.Count > 1)
                return new ProviderResolution(null, taxMatches, "El CIF/NIF/VAT coincide con varios proveedores activos.",
                    new ProviderResolutionDiagnostic(taxId ?? "", normalizedTaxId, null, "VAT exacto ambiguo", "Baja"));
        }

        string[] names = new[] { legalName, tradeName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var equivalent = new Dictionary<int, ProviderMatch>();
        var related = new Dictionary<int, ProviderMatch>();
        foreach (string name in names)
        {
            string normalizedName = NormalizeName(name);
            string[] tokens = SignificantTokens(normalizedName);
            foreach (string token in tokens.DefaultIfEmpty(normalizedName))
            {
                foreach (ProviderMatch candidate in ReadMatches(connection, SelectByNameSql, "@NamePattern", $"%{EscapeLikePattern(token)}%"))
                {
                    related[candidate.CompanyID] = candidate with { MatchType = "NameCandidate" };
                    if (SignificantTokens(NormalizeName(candidate.CompanyName)).SequenceEqual(tokens, StringComparer.Ordinal))
                        equivalent[candidate.CompanyID] = candidate with { MatchType = "NameNormalizedEquivalent" };
                }
            }
        }

        string detectedName = names.FirstOrDefault() ?? "";
        string normalizedDetectedName = NormalizeName(detectedName);
        if (equivalent.Count == 1)
        {
            ProviderMatch selected = equivalent.Values.Single();
            string reason = taxWasProvided
                ? "VAT exacto no encontrado; nombre normalizado equivalente único."
                : "Nombre normalizado equivalente único.";
            return new ProviderResolution(selected, [selected], reason,
                new ProviderResolutionDiagnostic(detectedName, normalizedDetectedName, selected.CompanyName, reason, "Alta"));
        }
        if (equivalent.Count > 1)
            return new ProviderResolution(null, equivalent.Values.ToList(), "La búsqueda por nombre normalizado no es inequívoca.",
                new ProviderResolutionDiagnostic(detectedName, normalizedDetectedName, null, "Varios candidatos equivalentes", "Baja"));

        if (related.Count == 0)
            return new ProviderResolution(null, [], taxWasProvided
                ? "El VAT exacto no existe y no se encontró candidato por nombre."
                : "No se encontró un proveedor activo.",
                new ProviderResolutionDiagnostic(detectedName, normalizedDetectedName, null, "Sin candidato equivalente", "Baja"));

        return new ProviderResolution(null, related.Values.ToList(), "Se muestran candidatos relacionados; no se selecciona automáticamente.",
            new ProviderResolutionDiagnostic(detectedName, normalizedDetectedName, null, "Coincidencia parcial controlada", "Media"));
    }

    public ProviderMatch? ResolveById(SqlConnection connection, int companyId)
    {
        using var command = new SqlCommand(SelectByIdSql, connection);
        command.Parameters.Add("@CompanyID", System.Data.SqlDbType.Int).Value = companyId;
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ProviderMatch(reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1), "ManualId") : null;
    }

    public ProviderMatch? Resolve(SqlConnection connection, SqlTransaction transaction, string providerName)
    {
        using var command = new SqlCommand(SelectByNameSql, connection, transaction);
        command.Parameters.Add("@NamePattern", System.Data.SqlDbType.NVarChar, 255).Value = $"%{EscapeLikePattern(providerName)}%";
        using var reader = command.ExecuteReader();
        var matches = new List<ProviderMatch>();
        while (reader.Read()) matches.Add(new ProviderMatch(reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1), "LegacyName"));
        return matches.FirstOrDefault(match => NormalizeName(match.CompanyName) == NormalizeName(providerName)) ?? (matches.Count == 1 ? matches[0] : null);
    }

    public static string NormalizeCompanyName(string value) => NormalizeName(value);

    private static List<ProviderMatch> ReadMatches(SqlConnection connection, string sql, string parameterName, string parameterValue)
    {
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(parameterName, System.Data.SqlDbType.NVarChar, 255).Value = parameterValue;
        using var reader = command.ExecuteReader();
        var matches = new List<ProviderMatch>();
        while (reader.Read()) matches.Add(new ProviderMatch(reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1), "Candidate"));
        return matches;
    }

    private static string EscapeLikePattern(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static string NormalizeIdentifier(string? value) => string.IsNullOrWhiteSpace(value) ? "" : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string NormalizeName(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            result.Append(char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ');
        }
        string[] ignored = ["LLC", "LTD", "LIMITED", "INC", "OPCO", "CORP", "CORPORATION", "SA", "SL", "GMBH"];
        string[] rawTokens = result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokens = new List<string>();
        for (int index = 0; index < rawTokens.Length; index++)
        {
            if (index + 1 < rawTokens.Length && rawTokens[index] is "S" or "L" && rawTokens[index + 1] is "A" or "L")
            {
                index++;
                continue;
            }
            tokens.Add(rawTokens[index]);
        }
        return string.Join(' ', tokens.Where(token => !ignored.Contains(token, StringComparer.Ordinal)));
    }

    private static string[] SignificantTokens(string normalizedName) => normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length >= 2).ToArray();
}

public sealed record ProviderResolutionDiagnostic(string DetectedName, string NormalizedName, string? SelectedCandidate, string Reason, string Confidence);

public sealed record ProviderMatch(int CompanyID, string CompanyName, string MatchType);

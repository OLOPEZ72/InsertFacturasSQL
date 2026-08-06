using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace InsertFacturasSQL.Services;

public sealed class ProviderResolver : IProviderResolver
{
    private const string SelectByIdSql = """
SELECT TOP (1) CompanyID, Name
FROM dbo.Companies
WHERE CompanyID = @CompanyID;
""";
    private const string SelectByTaxIdSql = """
SELECT TOP (10) CompanyID, Name
FROM dbo.Companies
WHERE Provider = 1
  AND Disabled IS NULL
  AND UPPER(REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(CIF)), ' ', ''), '-', ''), '.', ''), '/', '')) = @TaxId
ORDER BY CompanyID;
""";

    private const string SelectByNameSql = """
SELECT TOP (10) CompanyID, Name
FROM dbo.Companies
WHERE Provider = 1
  AND Disabled IS NULL
  AND Name LIKE @NamePattern ESCAPE '\'
ORDER BY CompanyID;
""";

    public ProviderResolution Resolve(
        SqlConnection connection,
        string? taxId,
        string? legalName,
        string? tradeName)
    {
        string normalizedTaxId = NormalizeIdentifier(taxId);
        if (normalizedTaxId.Length > 0)
        {
            var taxMatches = ReadMatches(connection, SelectByTaxIdSql, "@TaxId", normalizedTaxId);
            if (taxMatches.Count == 1)
            {
                return new ProviderResolution(taxMatches[0] with { MatchType = "TaxIdExact" }, taxMatches, null);
            }

            if (taxMatches.Count > 1)
            {
                return new ProviderResolution(
                    null,
                    taxMatches,
                    "El CIF/NIF/VAT coincide con varios proveedores activos.");
            }
        }

        var names = new[] { legalName, tradeName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var allCandidates = new Dictionary<int, ProviderMatch>();
        foreach (string name in names)
        {
            string pattern = $"%{EscapeLikePattern(name)}%";
            var candidates = ReadMatches(connection, SelectByNameSql, "@NamePattern", pattern);

            var exact = candidates
                .Where(candidate => NormalizeName(candidate.CompanyName) == NormalizeName(name))
                .ToList();

            if (exact.Count == 1)
            {
                return new ProviderResolution(exact[0] with { MatchType = "NameExact" }, exact, null);
            }

            foreach (var candidate in candidates)
            {
                allCandidates[candidate.CompanyID] = candidate with { MatchType = "NamePartial" };
            }
        }

        if (allCandidates.Count == 0)
        {
            string? token = names
                .Select(FindSignificantToken)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (token is not null)
            {
                var related = ReadMatches(
                    connection,
                    SelectByNameSql,
                    "@NamePattern",
                    $"%{EscapeLikePattern(token)}%");
                foreach (var candidate in related)
                {
                    allCandidates[candidate.CompanyID] = candidate with { MatchType = "SuggestedByToken" };
                }

                if (allCandidates.Count > 0)
                {
                    return new ProviderResolution(
                        null,
                        allCandidates.Values.ToList(),
                        "No existe una coincidencia exacta; se muestran candidatos relacionados sin seleccionar ninguno automáticamente.");
                }
            }
        }

        if (allCandidates.Count == 1)
        {
            return new ProviderResolution(null, allCandidates.Values.ToList(), "La coincidencia parcial no tiene confianza suficiente; confirme el proveedor.");
        }

        return allCandidates.Count > 1
            ? new ProviderResolution(null, allCandidates.Values.ToList(), "La búsqueda por nombre no es inequívoca.")
            : new ProviderResolution(null, [], "No se encontró un proveedor activo.");
    }

    public ProviderMatch? ResolveById(SqlConnection connection, int companyId)
    {
        using var command = new SqlCommand(SelectByIdSql, connection);
        command.Parameters.Add("@CompanyID", System.Data.SqlDbType.Int).Value = companyId;
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ProviderMatch(reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1), "ManualId")
            : null;
    }

    // Compatibilidad con el importador heredado. Program.cs ya no expone ese flujo de escritura.
    public ProviderMatch? Resolve(
        SqlConnection connection,
        SqlTransaction transaction,
        string providerName)
    {
        const string sql = """
SELECT TOP (10) CompanyID, Name
FROM dbo.Companies
WHERE Provider = 1
  AND Disabled IS NULL
  AND Name LIKE @NamePattern ESCAPE '\'
ORDER BY CompanyID;
""";

        using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@NamePattern", System.Data.SqlDbType.NVarChar, 255).Value =
            $"%{EscapeLikePattern(providerName)}%";

        using var reader = command.ExecuteReader();
        var matches = new List<ProviderMatch>();
        while (reader.Read())
        {
            matches.Add(new ProviderMatch(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                "LegacyName"));
        }

        var exact = matches.FirstOrDefault(match => NormalizeName(match.CompanyName) == NormalizeName(providerName));
        return exact ?? (matches.Count == 1 ? matches[0] : null);
    }

    private static List<ProviderMatch> ReadMatches(
        SqlConnection connection,
        string sql,
        string parameterName,
        string parameterValue)
    {
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(parameterName, System.Data.SqlDbType.NVarChar, 255).Value = parameterValue;

        using var reader = command.ExecuteReader();
        var matches = new List<ProviderMatch>();
        while (reader.Read())
        {
            matches.Add(new ProviderMatch(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                "Candidate"));
        }

        return matches;
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);

    private static string NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string NormalizeName(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);

        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            result.Append(char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ');
        }

        return string.Join(' ', result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? FindSignificantToken(string value)
    {
        string[] ignored = ["LLC", "LTD", "LIMITED", "INC", "CORP", "CORPORATION", "SL", "SA", "GMBH", "BV", "SARL"];
        return NormalizeName(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 4 && !ignored.Contains(token, StringComparer.Ordinal))
            .OrderByDescending(token => token.Length)
            .FirstOrDefault();
    }
}

public sealed record ProviderMatch(int CompanyID, string CompanyName, string MatchType);

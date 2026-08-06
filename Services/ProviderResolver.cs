using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace InsertFacturasSQL.Services;

public sealed class ProviderResolver
{
    public ProviderMatch? Resolve(SqlConnection connection, SqlTransaction transaction, string providerName)
    {
        var companies = GetCompanies(connection, transaction);
        string normalized = Normalize(providerName);

        var exact = companies.FirstOrDefault(company => Normalize(company.Name) == normalized);
        if (exact.CompanyID != 0)
        {
            return new ProviderMatch(exact.CompanyID, exact.Name, "Exact");
        }

        var partial = companies.FirstOrDefault(company =>
            Normalize(company.Name).Contains(normalized) ||
            normalized.Contains(Normalize(company.Name)));

        if (partial.CompanyID != 0)
        {
            return new ProviderMatch(partial.CompanyID, partial.Name, "Partial");
        }

        var tokens = GetTokens(providerName);
        var ranked = companies
            .Select(company => new
            {
                company.CompanyID,
                company.Name,
                Score = tokens.Count(token => GetTokens(company.Name).Contains(token))
            })
            .OrderByDescending(company => company.Score)
            .FirstOrDefault();

        if (ranked is not null && ranked.Score > 0)
        {
            return new ProviderMatch(ranked.CompanyID, ranked.Name, "Token");
        }

        var fallback = companies.FirstOrDefault(company => company.Name.ToUpper().Trim() == "VARIOS PROVEEDORES");

        return fallback.CompanyID != 0
            ? new ProviderMatch(fallback.CompanyID, fallback.Name, "Fallback")
            : null;
    }

    private static List<(int CompanyID, string Name)> GetCompanies(
        SqlConnection connection,
        SqlTransaction transaction)
    {
        var companies = new List<(int, string)>();

        using var command = new SqlCommand(
            "SELECT CompanyID, Name FROM Companies WHERE Disabled IS NULL",
            connection,
            transaction);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            companies.Add((reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }

        return companies;
    }

    private static string Normalize(string input)
    {
        input = input.ToUpper();
        input = Regex.Replace(input, @"[^\w\s]", " ");
        return Regex.Replace(input, @"\s+", " ").Trim();
    }

    private static List<string> GetTokens(string input)
    {
        return Normalize(input)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 2)
            .ToList();
    }
}

public sealed record ProviderMatch(int CompanyID, string CompanyName, string MatchType);

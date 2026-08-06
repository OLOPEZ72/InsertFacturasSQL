using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Iniciando programa...");

        string folderPath = Path.Combine(Directory.GetCurrentDirectory(), "JSON");
        Console.WriteLine($"Buscando JSON en: {folderPath}");

        string? connectionString = Environment.GetEnvironmentVariable("INSERT_FACTURAS_SQL_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("No se ha configurado la variable de entorno INSERT_FACTURAS_SQL_CONNECTION_STRING.");
            return;
        }

        try
        {
            if (!Directory.Exists(folderPath))
            {
                Console.WriteLine("Carpeta no encontrada");
                return;
            }

            var files = Directory.GetFiles(folderPath, "*.json");

            if (files.Length == 0)
            {
                Console.WriteLine("No hay archivos JSON");
                return;
            }

            using var conn = new SqlConnection(connectionString);
            conn.Open();
            Console.WriteLine("Conexion SQL correcta");

            foreach (var jsonPath in files)
            {
                Console.WriteLine("=================================");
                Console.WriteLine($"Procesando: {jsonPath}");

                using var tx = conn.BeginTransaction();

                try
                {
                    string json = File.ReadAllText(jsonPath);

                    var factura = JsonSerializer.Deserialize<Factura>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (factura == null)
                        throw new Exception("Error deserializando JSON");

                    if (factura.Items == null || factura.Items.Count == 0)
                        throw new Exception("Factura sin items");

                    Console.WriteLine($"Proveedor: {factura.ProviderName}");

                    var providerMatch = ResolveCompany(conn, tx, factura.ProviderName);

                    if (providerMatch == null)
                        throw new Exception("Proveedor no encontrado");

                    Console.WriteLine($"CompanyID: {providerMatch.CompanyID}");

                    int orderId = InsertProviderOrder(conn, tx, factura, providerMatch.CompanyID);

                    foreach (var item in factura.Items)
                    {
                        InsertItem(conn, tx, orderId, factura.CProjectID, item);
                    }

                    tx.Commit();
                    Console.WriteLine($"Insert OK. ID = {orderId}");
                }
                catch (Exception ex)
                {
                    tx.Rollback();
                    Console.WriteLine("Error:");
                    Console.WriteLine(ex.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error general:");
            Console.WriteLine(ex.ToString());
        }

        Console.WriteLine("Fin del proceso");
        Console.ReadKey();
    }

    // ===================== PROVIDER MATCH =====================

    public class CompanyMatch
    {
        public int CompanyID { get; set; }
        public string CompanyName { get; set; } = "";
        public string MatchType { get; set; } = "";
    }

    static CompanyMatch? ResolveCompany(SqlConnection conn, SqlTransaction tx, string providerName)
    {
        var companies = GetCompanies(conn, tx);

        string normalized = Normalize(providerName);

        var exact = companies.FirstOrDefault(c => Normalize(c.Name) == normalized);
        if (exact.CompanyID != 0)
            return new CompanyMatch { CompanyID = exact.CompanyID, CompanyName = exact.Name, MatchType = "Exact" };

        var partial = companies.FirstOrDefault(c =>
            Normalize(c.Name).Contains(normalized) ||
            normalized.Contains(Normalize(c.Name)));

        if (partial.CompanyID != 0)
            return new CompanyMatch { CompanyID = partial.CompanyID, CompanyName = partial.Name, MatchType = "Partial" };

        var tokens = GetTokens(providerName);

        var ranked = companies
            .Select(c => new
            {
                c.CompanyID,
                c.Name,
                Score = tokens.Count(t => GetTokens(c.Name).Contains(t))
            })
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (ranked != null && ranked.Score > 0)
            return new CompanyMatch { CompanyID = ranked.CompanyID, CompanyName = ranked.Name, MatchType = "Token" };

        var fallback = companies.FirstOrDefault(c => c.Name.ToUpper().Trim() == "VARIOS PROVEEDORES");

        if (fallback.CompanyID != 0)
            return new CompanyMatch { CompanyID = fallback.CompanyID, CompanyName = fallback.Name, MatchType = "Fallback" };

        return null;
    }

    static List<(int CompanyID, string Name)> GetCompanies(SqlConnection conn, SqlTransaction tx)
    {
        var list = new List<(int, string)>();

        using var cmd = new SqlCommand("SELECT CompanyID, Name FROM Companies WHERE Disabled IS NULL", conn, tx);
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            list.Add((reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }

        return list;
    }

    static string Normalize(string input)
    {
        input = input.ToUpper();
        input = Regex.Replace(input, @"[^\w\s]", " ");
        return Regex.Replace(input, @"\s+", " ").Trim();
    }

    static List<string> GetTokens(string input)
    {
        return Normalize(input)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length > 2)
            .ToList();
    }

    // ===================== INSERT =====================

    static int InsertProviderOrder(SqlConnection conn, SqlTransaction tx, Factura factura, int companyId)
    {
        const string sql = @"
INSERT INTO ProviderOrder
(
    ProviderOrderDate,
    UserID,
    Provider,
    Notes,
    SendedModeId,
    InitDescription,
    CreditCard,
    Paid,
    PaymentMethodID,
    PaymentNotes,
    PaymentDate,
    Charget,
    currencyID
)
VALUES
(
    @Date,
    5,
    @Provider,
    @Notes,
    1,
    @InitDescription,
    124,
    0,
    400,
    @PaymentNotes,
    @PaymentDate,
    0,
    1
);

SELECT CAST(SCOPE_IDENTITY() AS INT);
";

        using var cmd = new SqlCommand(sql, conn, tx);

        cmd.Parameters.AddWithValue("@Date", factura.Fecha);
        cmd.Parameters.AddWithValue("@Provider", companyId);
        cmd.Parameters.AddWithValue("@Notes", factura.Descripcion ?? "");
        cmd.Parameters.AddWithValue("@InitDescription", factura.Descripcion ?? "");

        cmd.Parameters.AddWithValue("@PaymentNotes",
            string.IsNullOrWhiteSpace(factura.PaymentNotes) ? DBNull.Value : factura.PaymentNotes);

        cmd.Parameters.AddWithValue("@PaymentDate",
            factura.PaymentDate.HasValue ? factura.PaymentDate.Value : DBNull.Value);

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    static void InsertItem(SqlConnection conn, SqlTransaction tx, int providerOrderId, int cProjectId, FacturaItem item)
    {
        const string sql = @"
INSERT INTO ItemsProviderOrder
(
    ProviderOrderID,
    CProjectID,
    Description,
    Amount,
    UnitPrice,
    Discount,
    IVA,
    Disabled
)
VALUES
(
    @ProviderOrderID,
    @CProjectID,
    @Description,
    @Amount,
    @UnitPrice,
    @Discount,
    @IVA,
    0
);";

        using var cmd = new SqlCommand(sql, conn, tx);

        cmd.Parameters.AddWithValue("@ProviderOrderID", providerOrderId);
        cmd.Parameters.AddWithValue("@CProjectID", cProjectId);
        cmd.Parameters.AddWithValue("@Description", item.Description ?? "");
        cmd.Parameters.AddWithValue("@Amount", item.Amount);
        cmd.Parameters.AddWithValue("@UnitPrice", item.UnitPrice);
        cmd.Parameters.AddWithValue("@Discount", item.Discount);
        cmd.Parameters.AddWithValue("@IVA", item.IVA);

        cmd.ExecuteNonQuery();
    }
}

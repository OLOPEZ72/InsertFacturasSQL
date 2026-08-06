using InsertFacturasSQL.Configuration;
using InsertFacturasSQL.Models;
using Microsoft.Data.SqlClient;

namespace InsertFacturasSQL.Services;

public sealed class SqlInvoiceImporter : IDisposable
{
    private readonly SqlConnection _connection;
    private readonly ProviderResolver _providerResolver;

    public SqlInvoiceImporter(DatabaseSettings settings, ProviderResolver providerResolver)
    {
        _connection = new SqlConnection(settings.ConnectionString);
        _providerResolver = providerResolver;
    }

    public void Open()
    {
        _connection.Open();
    }

    public int Import(Factura factura)
    {
        using var transaction = _connection.BeginTransaction();

        try
        {
            var providerMatch = _providerResolver.Resolve(
                _connection,
                transaction,
                factura.ProviderName);

            if (providerMatch is null)
            {
                throw new Exception("Proveedor no encontrado");
            }

            Console.WriteLine($"CompanyID: {providerMatch.CompanyID}");

            int orderId = InsertProviderOrder(transaction, factura, providerMatch.CompanyID);

            foreach (var item in factura.Items)
            {
                InsertItem(transaction, orderId, factura.CProjectID, item);
            }

            transaction.Commit();
            return orderId;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private int InsertProviderOrder(SqlTransaction transaction, Factura factura, int companyId)
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

        using var command = new SqlCommand(sql, _connection, transaction);

        command.Parameters.AddWithValue("@Date", factura.Fecha);
        command.Parameters.AddWithValue("@Provider", companyId);
        command.Parameters.AddWithValue("@Notes", factura.Descripcion ?? "");
        command.Parameters.AddWithValue("@InitDescription", factura.Descripcion ?? "");
        command.Parameters.AddWithValue(
            "@PaymentNotes",
            string.IsNullOrWhiteSpace(factura.PaymentNotes) ? DBNull.Value : factura.PaymentNotes);
        command.Parameters.AddWithValue(
            "@PaymentDate",
            factura.PaymentDate.HasValue ? factura.PaymentDate.Value : DBNull.Value);

        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void InsertItem(
        SqlTransaction transaction,
        int providerOrderId,
        int cProjectId,
        FacturaItem item)
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

        using var command = new SqlCommand(sql, _connection, transaction);

        command.Parameters.AddWithValue("@ProviderOrderID", providerOrderId);
        command.Parameters.AddWithValue("@CProjectID", cProjectId);
        command.Parameters.AddWithValue("@Description", item.Description ?? "");
        command.Parameters.AddWithValue("@Amount", item.Amount);
        command.Parameters.AddWithValue("@UnitPrice", item.UnitPrice);
        command.Parameters.AddWithValue("@Discount", item.Discount);
        command.Parameters.AddWithValue("@IVA", item.IVA);

        command.ExecuteNonQuery();
    }
}

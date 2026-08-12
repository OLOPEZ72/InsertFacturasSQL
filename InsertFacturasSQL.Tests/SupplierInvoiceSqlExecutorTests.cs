using Microsoft.Data.SqlClient;
using InsertFacturasSQL.Models;
using InsertFacturasSQL.Services;

namespace InsertFacturasSQL.Tests;

public sealed class SupplierInvoiceSqlExecutorTests
{
    [Fact]
    public void Execute_DoesNotOpenConnectionWhenPreviewIsInvalid()
    {
        var draft = new SupplierInvoiceDraft();
        draft.ValidationErrors.Add("Factura inválida");

        using var connection = new SqlConnection();
        var executor = new SupplierInvoiceSqlExecutor();

        Assert.Throws<InvalidOperationException>(() => executor.Execute(connection, draft));
        Assert.False(connection.State == System.Data.ConnectionState.Open);
    }
}

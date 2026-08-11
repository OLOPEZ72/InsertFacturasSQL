using Microsoft.Data.SqlClient;
using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public interface ISupplierInvoiceSqlExecutor
{
    int Execute(SqlConnection connection, SupplierInvoiceDraft draft);
}

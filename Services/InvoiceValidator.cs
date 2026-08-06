using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public sealed class InvoiceValidator
{
    public Factura Validate(Factura? factura)
    {
        if (factura is null)
        {
            throw new Exception("Error deserializando JSON");
        }

        if (factura.Items is null || factura.Items.Count == 0)
        {
            throw new Exception("Factura sin items");
        }

        return factura;
    }
}

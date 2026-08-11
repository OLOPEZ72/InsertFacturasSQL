namespace InsertFacturasSQL.Models;

public sealed class Factura
{
    public string ProviderName { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string Descripcion { get; set; } = "";
    public int CProjectID { get; set; }
    public DateTime? PaymentDate { get; set; }
    public string? PaymentNotes { get; set; }
    public List<FacturaItem> Items { get; set; } = new();
}

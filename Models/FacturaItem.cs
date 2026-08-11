namespace InsertFacturasSQL.Models;

public sealed class FacturaItem
{
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal IVA { get; set; }
}

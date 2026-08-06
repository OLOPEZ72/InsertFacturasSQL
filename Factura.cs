using System;
using System.Collections.Generic;

public class Factura
{
    public string ProviderName { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string Descripcion { get; set; } = "";
    public int CProjectID { get; set; }
    public DateTime? PaymentDate { get; set; }
    public string? PaymentNotes { get; set; }
    public List<FacturaItem> Items { get; set; } = new();
}

public class FacturaItem
{
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal IVA { get; set; }
}
using System.Data;
using System.Text;
using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public sealed class SupplierInvoiceSqlPreviewGenerator : ISupplierInvoiceSqlPreviewGenerator
{
    public const string PreviewWarning = "MODO VISTA PREVIA: NO SE HA MODIFICADO LA BASE DE DATOS";

    public SupplierInvoiceSqlPreview Generate(SupplierInvoiceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var errors = ValidateForPreview(draft);
        if (errors.Count > 0)
        {
            return new SupplierInvoiceSqlPreview("", [], errors);
        }

        var parameters = BuildParameters(draft);
        var sql = new StringBuilder();
        sql.AppendLine("SET XACT_ABORT ON;");
        sql.AppendLine("BEGIN TRY");
        sql.AppendLine("    BEGIN TRANSACTION;");
        sql.AppendLine();
        sql.AppendLine("    INSERT INTO dbo.ProviderOrder");
        sql.AppendLine("    (");
        sql.AppendLine("        ProviderOrderDate, UserID, Provider, Notes, SendedModeId,");
        sql.AppendLine("        InitDescription, CreditCard, Paid, PaymentMethodID,");
        sql.AppendLine("        PaymentNotes, PaymentDate, Charget, currencyID");
        sql.AppendLine("    )");
        sql.AppendLine("    VALUES");
        sql.AppendLine("    (");
        sql.AppendLine("        @ProviderOrderDate, @UserID, @Provider, @Notes, @SendedModeId,");
        sql.AppendLine("        @InitDescription, @CreditCard, @Paid, @PaymentMethodID,");
        sql.AppendLine("        @PaymentNotes, @PaymentDate, @Charget, @CurrencyID");
        sql.AppendLine("    );");
        sql.AppendLine();
        sql.AppendLine("    DECLARE @ProviderOrderID int = CONVERT(int, SCOPE_IDENTITY());");

        for (int index = 0; index < draft.Items.Count; index++)
        {
            int number = index + 1;
            string prefix = $"@Item{number}";
            sql.AppendLine();
            sql.AppendLine("    INSERT INTO dbo.ItemsProviderOrder");
            sql.AppendLine("    (");
            sql.AppendLine("        ProviderOrderID, CProjectID, Description, Amount,");
            sql.AppendLine("        UnitPrice, Discount, IVA, Disabled");
            sql.AppendLine("    )");
            sql.AppendLine("    VALUES");
            sql.AppendLine("    (");
            sql.AppendLine($"        @ProviderOrderID, {prefix}CProjectID, {prefix}Description, {prefix}Amount,");
            sql.AppendLine($"        {prefix}UnitPrice, {prefix}Discount, {prefix}IVA, {prefix}Disabled");
            sql.AppendLine("    );");
        }

        sql.AppendLine();
        sql.AppendLine("    -- ValidaciÃ³n post-inserciÃ³n sobre los valores realmente almacenados");
        sql.AppendLine("    DECLARE @PersistedBaseTotal decimal(18,2);");
        sql.AppendLine("    DECLARE @PersistedTaxTotal decimal(18,2);");
        sql.AppendLine("    DECLARE @PersistedFinalTotal decimal(18,2);");
        sql.AppendLine("    SELECT");
        sql.AppendLine("        @PersistedBaseTotal = COALESCE(SUM(ROUND(Amount * UnitPrice * (1 - ISNULL(Discount, 0) / 100), 2)), 0),");
        sql.AppendLine("        @PersistedTaxTotal = COALESCE(SUM(ROUND(ROUND(Amount * UnitPrice * (1 - ISNULL(Discount, 0) / 100), 2) * IVA / 100, 2)), 0)");
        sql.AppendLine("    FROM dbo.ItemsProviderOrder");
        sql.AppendLine("    WHERE ProviderOrderID = @ProviderOrderID AND Disabled = 0;");
        sql.AppendLine("    SET @PersistedFinalTotal = @PersistedBaseTotal + @PersistedTaxTotal;");
        sql.AppendLine("    IF ABS(@PersistedFinalTotal - @ExpectedInvoiceTotal) > @InvoiceTotalTolerance");
        sql.AppendLine("    BEGIN");
        sql.AppendLine("        SELECT @ProviderOrderID AS ProviderOrderID, Description, Amount, UnitPrice, Discount, IVA, CProjectID,");
        sql.AppendLine("            ROUND(Amount * UnitPrice * (1 - ISNULL(Discount, 0) / 100), 2) AS CalculatedBase,");
        sql.AppendLine("            ROUND(ROUND(Amount * UnitPrice * (1 - ISNULL(Discount, 0) / 100), 2) * IVA / 100, 2) AS CalculatedTax");
        sql.AppendLine("        FROM dbo.ItemsProviderOrder");
        sql.AppendLine("        WHERE ProviderOrderID = @ProviderOrderID AND Disabled = 0;");
        sql.AppendLine("        SELECT @PersistedBaseTotal AS PersistedBaseTotal, @PersistedTaxTotal AS PersistedTaxTotal,");
        sql.AppendLine("            @PersistedFinalTotal AS PersistedFinalTotal, @ExpectedInvoiceTotal AS ExpectedInvoiceTotal,");
        sql.AppendLine("            ABS(@PersistedFinalTotal - @ExpectedInvoiceTotal) AS Difference, @InvoiceTotalTolerance AS Tolerance;");
        sql.AppendLine("        RAISERROR ('El total persistido no coincide con el total de la factura.', 16, 1);");
        sql.AppendLine("    END;");
        sql.AppendLine("    SELECT @ProviderOrderID AS ProviderOrderID;");

        sql.AppendLine();
        sql.AppendLine("    COMMIT TRANSACTION;");
        sql.AppendLine("END TRY");
        sql.AppendLine("BEGIN CATCH");
        sql.AppendLine("    DECLARE @ErrorMessage nvarchar(4000) = ERROR_MESSAGE();");
        sql.AppendLine("    DECLARE @ErrorSeverity int = ERROR_SEVERITY();");
        sql.AppendLine("    DECLARE @ErrorState int = ERROR_STATE();");
        sql.AppendLine("    IF @@TRANCOUNT > 0");
        sql.AppendLine("        ROLLBACK TRANSACTION;");
        sql.AppendLine("    RAISERROR (@ErrorMessage, @ErrorSeverity, @ErrorState);");
        sql.AppendLine("END CATCH;");

        return new SupplierInvoiceSqlPreview(sql.ToString(), parameters, []);
    }

    private static List<string> ValidateForPreview(SupplierInvoiceDraft draft)
    {
        var errors = new List<string>(draft.ValidationErrors);

        AddIf(errors, draft.CompanyId is null or <= 0, "Proveedor no resuelto.");
        AddIf(errors, draft.CommercialProjectId is null or <= 0, "Proyecto comercial no válido.");
        AddIf(errors, !draft.CommercialProjectIsValid, "Proyecto comercial no disponible para nuevas facturas.");
        AddIf(errors, string.IsNullOrWhiteSpace(draft.InvoiceNumber), "Número de factura obligatorio.");
        AddIf(errors, draft.InvoiceDate < new DateTime(1900, 1, 1), "Fecha de factura obligatoria.");
        AddIf(errors, draft.Items.Count == 0, "La factura debe contener al menos un item.");
        AddIf(errors, draft.Notes.Length > 250, "Notes supera los 250 caracteres.");
        AddIf(errors, draft.InitDescription.Length > 900, "InitDescription supera los 900 caracteres.");
        AddIf(errors, draft.PaymentNotes?.Length > 250, "PaymentNotes supera los 250 caracteres.");

        foreach (var item in draft.Items)
        {
            AddIf(errors, string.IsNullOrWhiteSpace(item.Description), $"Item {item.Position}: descripción obligatoria.");
            AddIf(errors, item.Description.Length > 250, $"Item {item.Position}: descripción demasiado larga.");
            AddIf(errors, item.Amount <= 0, $"Item {item.Position}: Amount debe ser mayor que cero.");
            AddIf(errors, item.UnitPrice < 0, $"Item {item.Position}: UnitPrice no puede ser negativo.");
            AddIf(errors, item.IVA is < 0 or > 100, $"Item {item.Position}: IVA no válido.");
        }

        return errors.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void AddIf(List<string> errors, bool condition, string error)
    {
        if (condition)
        {
            errors.Add(error);
        }
    }

    private static List<SqlPreviewParameter> BuildParameters(SupplierInvoiceDraft draft)
    {
        var result = new List<SqlPreviewParameter>
        {
            new("@ProviderOrderDate", draft.InvoiceDate, SqlDbType.SmallDateTime),
            new("@UserID", draft.UserId, SqlDbType.Int),
            new("@Provider", draft.CompanyId!.Value, SqlDbType.Int),
            new("@Notes", draft.Notes, SqlDbType.NVarChar, Size: 250),
            new("@SendedModeId", draft.SendedModeId, SqlDbType.Int),
            new("@InitDescription", draft.InitDescription, SqlDbType.NVarChar, Size: 900),
            new("@CreditCard", draft.CreditCardId, SqlDbType.Int),
            new("@Paid", draft.Paid, SqlDbType.Bit),
            new("@PaymentMethodID", draft.PaymentMethodId, SqlDbType.Int),
            new("@PaymentNotes", draft.PaymentNotes, SqlDbType.NVarChar, Size: 250),
            new("@PaymentDate", draft.PaymentDate, SqlDbType.DateTime),
            new("@Charget", draft.Charget, SqlDbType.Bit),
            new("@CurrencyID", draft.CurrencyId, SqlDbType.Int)
        };

        result.Add(new("@ExpectedInvoiceTotal", draft.DocumentTotal ?? draft.CalculatedTotal, SqlDbType.Decimal, Precision: 18, Scale: 2));
        result.Add(new("@InvoiceTotalTolerance", SupplierInvoicePostInsertValidator.DefaultTolerance, SqlDbType.Decimal, Precision: 18, Scale: 2));

        for (int index = 0; index < draft.Items.Count; index++)
        {
            var item = draft.Items[index];
            string prefix = $"@Item{index + 1}";
            result.Add(new($"{prefix}CProjectID", item.CommercialProjectId!.Value, SqlDbType.Int));
            result.Add(new($"{prefix}Description", item.Description, SqlDbType.NVarChar, Size: 250));
            result.Add(new($"{prefix}Amount", item.Amount, SqlDbType.Decimal, Precision: 18, Scale: 2));
            result.Add(new($"{prefix}UnitPrice", item.PersistedUnitPrice, SqlDbType.Decimal, Precision: 18, Scale: 3));
            result.Add(new($"{prefix}Discount", item.DiscountPercent, SqlDbType.Decimal, Precision: 18, Scale: 2));
            result.Add(new($"{prefix}IVA", item.IVA, SqlDbType.Decimal, Precision: 18, Scale: 2));
            result.Add(new($"{prefix}Disabled", false, SqlDbType.Bit));
        }

        return result;
    }
}

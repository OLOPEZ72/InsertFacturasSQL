namespace InsertFacturasSQL.Configuration;

public sealed class DatabaseSettings
{
    public const string ConnectionStringEnvironmentVariable = "INSERT_FACTURAS_SQL_CONNECTION_STRING";

    private DatabaseSettings(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static DatabaseSettings? FromEnvironment()
    {
        string? connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);

        return string.IsNullOrWhiteSpace(connectionString)
            ? null
            : new DatabaseSettings(connectionString);
    }
}

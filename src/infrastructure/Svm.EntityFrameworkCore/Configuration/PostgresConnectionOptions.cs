using Npgsql;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Configuration;

public sealed class PostgresConnectionOptions
{
    private PostgresConnectionOptions(string connectionString) => ConnectionString = connectionString;
    internal string ConnectionString { get; }
    public override string ToString() => "PostgreSQL connection configuration (redacted)";

    public static PostgresConnectionOptions Parse(string? input)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException();
            var builder = new NpgsqlConnectionStringBuilder(input);
            if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database) ||
                string.IsNullOrWhiteSpace(builder.Username) || string.IsNullOrEmpty(builder.Password) ||
                builder.IncludeErrorDetail || builder.LogParameters || builder.NoResetOnClose || builder.Multiplexing ||
                builder.Timeout <= 0 || builder.CommandTimeout <= 0 || builder.MaxPoolSize < 1)
                throw new ArgumentException();
            builder.Enlist = false;
            builder.ApplicationName = "Svm.Persistence";
            return new PostgresConnectionOptions(builder.ConnectionString);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            // Npgsql parser messages can contain the submitted configuration. Do not retain an inner exception.
            throw new PersistenceException(PersistenceFailure.ConfigurationInvalid);
        }
    }
}

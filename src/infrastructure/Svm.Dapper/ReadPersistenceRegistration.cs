using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.Services.Contracts.Framework;

namespace Svm.Dapper;

public static class ReadPersistenceRegistration
{
    public static IServiceCollection AddSvmReadPersistence(this IServiceCollection services, string connectionString)
    {
        if (services.Any(d => d.ServiceType == typeof(ReadDataSource)))
            throw new InvalidOperationException("Read persistence is already registered.");
        services.AddSingleton(ReadConnectionOptions.Parse(connectionString));
        services.AddSingleton<ReadDataSource>();
        services.AddScoped<ReadQuerySession>();
        return services;
    }
}

internal sealed class ReadConnectionOptions
{
    private ReadConnectionOptions(string value) => ConnectionString = value;
    internal string ConnectionString { get; }
    public override string ToString() => "PostgreSQL read connection configuration (redacted)";
    internal static ReadConnectionOptions Parse(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException();
            var builder = new NpgsqlConnectionStringBuilder(value);
            if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database) ||
                string.IsNullOrWhiteSpace(builder.Username) || string.IsNullOrEmpty(builder.Password) ||
                builder.IncludeErrorDetail || builder.LogParameters || builder.NoResetOnClose || builder.Multiplexing ||
                builder.Timeout <= 0 || builder.CommandTimeout <= 0 || builder.MaxPoolSize < 1) throw new ArgumentException();
            builder.Enlist = false;
            builder.ApplicationName = "Svm.ReadQueries";
            return new(builder.ConnectionString);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        { throw new PersistenceException(PersistenceFailure.ConfigurationInvalid); }
    }
}

internal sealed class ReadDataSource : IAsyncDisposable
{
    public ReadDataSource(ReadConnectionOptions options) => DataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    internal NpgsqlDataSource DataSource { get; }
    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

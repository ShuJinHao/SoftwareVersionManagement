using Npgsql;

namespace Svm.Dapper;

/// <summary>Bounded, read-only deployment probe. Never migrates or writes business data.</summary>
public sealed class ReadPersistenceProbe : IAsyncDisposable
{
    private readonly ReadDataSource _source;
    private readonly ReadQuerySession _query;
    public ReadPersistenceProbe(string connectionString)
    {
        var options = new NpgsqlConnectionStringBuilder(ReadConnectionOptions.Parse(connectionString).ConnectionString)
            { MaxPoolSize = 2, Timeout = 3, CommandTimeout = 3 };
        _source = new ReadDataSource(ReadConnectionOptions.Parse(options.ConnectionString));
        _query = new ReadQuerySession(_source);
    }
    public async Task<bool> CheckAsync(CancellationToken token)
    {
        try
        {
            var result = await _query.QueryAsync<bool>("SELECT count(*) = 12 AND max(\"MigrationId\") = '20261021000100_HardwareWorkspace' FROM framework.\"__EFMigrationsHistory\"", null, token);
            return result.Count == 1 && result[0];
        }
        catch (Exception e) when (e is Svm.Services.Contracts.Framework.PersistenceException or IOException or OperationCanceledException) { return false; }
    }
    public ValueTask DisposeAsync() => _source.DisposeAsync();
}

using Npgsql;
using Svm.EntityFrameworkCore.Configuration;

namespace Svm.EntityFrameworkCore.Framework;

internal sealed class WriteDataSource : IAsyncDisposable
{
    public WriteDataSource(PostgresConnectionOptions options) => DataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    internal NpgsqlDataSource DataSource { get; }
    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

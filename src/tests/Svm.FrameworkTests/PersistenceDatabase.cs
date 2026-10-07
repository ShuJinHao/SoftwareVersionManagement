using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.Dapper;
using Svm.EntityFrameworkCore;
using Svm.EntityFrameworkCore.Framework;
using Svm.EntityFrameworkCore.Migrations;
using Svm.Services.CrossCutting.DomainEvents;
using Xunit;

namespace Svm.FrameworkTests;

/// <summary>Every instance owns only its random database and roles in the dedicated local container.</summary>
public sealed class PersistenceDatabase : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly List<string> _createdRoles = [];
    private string _admin = "";
    private bool _createdDatabase;
    internal string DatabaseName => $"svm_test_{_suffix}";
    internal string MigrationRole => $"svmt_{_suffix}_m";
    internal string WriterRole => $"svmt_{_suffix}_w";
    internal string ReaderRole => $"svmt_{_suffix}_r";
    internal string MigrationConnection { get; private set; } = "";
    internal string WriterConnection { get; private set; } = "";
    internal string ReaderConnection { get; private set; } = "";
    internal MigrationRunner Runner => new(MigrationConfiguration.Create(MigrationConnection, WriterRole, ReaderRole));

    public Task InitializeAsync() => CreateAsync(migrate: true, probe: true);

    internal async Task CreateAsync(bool migrate, bool probe = false)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("SVM_TEST_DATABASE_CONFIG_FILE")
                ?? throw new InvalidOperationException("Run eng/postgres up and eng/postgres test framework for real PostgreSQL verification.");
            using var file = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var settings = new NpgsqlConnectionStringBuilder(file.RootElement.GetProperty("connectionString").GetString());
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "build/postgres.local.json"))) root = root.Parent;
            if (root is null) throw new InvalidOperationException("SVM project root was not found.");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.FullName))).ToLowerInvariant()[..12];
            if (file.RootElement.GetProperty("projectIdentity").GetString() != identity ||
                settings.Host != "127.0.0.1" || settings.Database != "postgres" || settings.Username != "svm_bootstrap")
                throw new InvalidOperationException("Tests require this project's dedicated local PostgreSQL configuration.");
            settings.Pooling = false;
            settings.IncludeErrorDetail = false;
            settings.LogParameters = false;
            _admin = settings.ConnectionString;
            if (await ScalarAsync<string>(_admin, "SHOW server_version_num") != "170011")
                throw new InvalidOperationException("Tests require PostgreSQL 17.11.");
            foreach (var role in new[] { MigrationRole, WriterRole, ReaderRole })
            {
                var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await ExecuteAsync(_admin, $"CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD '{password}'");
                _createdRoles.Add(role);
                var connection = new NpgsqlConnectionStringBuilder(_admin) { Database = DatabaseName, Username = role, Password = password }.ConnectionString;
                if (role == MigrationRole) MigrationConnection = connection;
                else if (role == WriterRole) WriterConnection = connection;
                else ReaderConnection = connection;
            }
            await ExecuteAsync(_admin, $"ALTER ROLE {ReaderRole} SET default_transaction_read_only=on");
            await ExecuteAsync(_admin, $"CREATE DATABASE {DatabaseName} OWNER {MigrationRole}");
            _createdDatabase = true;
            await ExecuteAsync(_admin, $"REVOKE ALL ON DATABASE {DatabaseName} FROM PUBLIC; GRANT CONNECT ON DATABASE {DatabaseName} TO {MigrationRole}, {WriterRole}, {ReaderRole}");
            await ExecuteAsync(MigrationConnection, "REVOKE CREATE ON SCHEMA public FROM PUBLIC");
            if (migrate) await Runner.ApplyAsync(default);
            if (probe)
                await ExecuteAsync(MigrationConnection, "CREATE TABLE iam.foundation_probe (id uuid PRIMARY KEY, value integer NOT NULL)");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    internal ServiceProvider CreateProvider(IInterceptor? interceptor = null, string? writer = null, string? reader = null)
    {
        var services = new ServiceCollection();
        services.AddSvmDomainEvents([]).ValidateSvmDomainEvents();
        if (interceptor is not null) services.AddSingleton(interceptor);
        services.AddSvmPostgres(RuntimeConnection(writer ?? WriterConnection));
        services.AddSvmReadPersistence(RuntimeConnection(reader ?? ReaderConnection));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static string RuntimeConnection(string input) => new NpgsqlConnectionStringBuilder(input)
        { Pooling = true, MinPoolSize = 0, MaxPoolSize = 2 }.ConnectionString;

    internal static Task InsertAsync(SvmDbContext context, Guid id, int value, CancellationToken token) =>
        context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO iam.foundation_probe (id,value) VALUES ({id},{value})", token);

    internal Task<long> CountAsync(Guid id) => ScalarAsync<long>(ReaderConnection,
        "SELECT count(*) FROM iam.foundation_probe WHERE id=@id", new NpgsqlParameter("id", id));

    internal static void AssertRedacted(string output, string secret)
    {
        // An assertion failure must not print either the secret or the unredacted diagnostic.
        var containsSecret = output.Contains(secret, StringComparison.Ordinal);
        Assert.False(containsSecret, "Diagnostic output must redact credentials.");
    }

    internal Task<bool> TerminateOwnConnectionAsync(int processId) => ScalarAsync<bool>(_admin,
        "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE pid=@pid AND datname=@database",
        new NpgsqlParameter("pid", processId), new NpgsqlParameter("database", DatabaseName));

    internal static async Task<T> ScalarAsync<T>(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected scalar result."));
    }

    internal static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_createdDatabase)
        {
            await ExecuteAsync(_admin, $"DROP DATABASE {DatabaseName} WITH (FORCE)");
            _createdDatabase = false;
        }
        foreach (var role in _createdRoles.ToArray())
        {
            await ExecuteAsync(_admin, $"DROP ROLE {role}");
            _createdRoles.Remove(role);
        }
    }
}

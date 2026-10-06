using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Svm.EntityFrameworkCore.Configuration;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

public sealed class MigrationConfiguration
{
    private MigrationConfiguration(PostgresConnectionOptions connection, string writerRole, string readerRole)
    { Connection = connection; WriterRole = writerRole; ReaderRole = readerRole; }
    internal PostgresConnectionOptions Connection { get; }
    internal string WriterRole { get; }
    internal string ReaderRole { get; }
    public override string ToString() => "Migration configuration (redacted)";

    public static MigrationConfiguration Create(string connectionString, string writerRole, string readerRole)
    {
        var connection = PostgresConnectionOptions.Parse(connectionString);
        var user = new NpgsqlConnectionStringBuilder(connection.ConnectionString).Username;
        if (!ValidRole(writerRole) || !ValidRole(readerRole) || writerRole == readerRole || writerRole == user || readerRole == user)
            throw new PersistenceException(PersistenceFailure.ConfigurationInvalid);
        return new(connection, writerRole, readerRole);
    }
    public static MigrationConfiguration Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var json = document.RootElement;
            var names = json.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 3 || names.Distinct().Count() != 3 || names.Except(["connectionString", "writerRole", "readerRole"]).Any())
                throw new PersistenceException(PersistenceFailure.ConfigurationInvalid);
            return Create(json.GetProperty("connectionString").GetString()!, json.GetProperty("writerRole").GetString()!, json.GetProperty("readerRole").GetString()!);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { throw new PersistenceException(PersistenceFailure.ConfigurationInvalid); }
    }
    private static bool ValidRole(string? role) => role is not null && Regex.IsMatch(role, "^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant) && role != "public";
}

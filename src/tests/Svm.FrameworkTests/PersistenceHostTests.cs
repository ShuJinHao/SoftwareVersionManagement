using System.Diagnostics;
using System.Text.Json;
using Svm.EntityFrameworkCore.Migrations;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class PersistenceHostTests
{
    [Theory]
    [InlineData("Svm.HttpApi")]
    [InlineData("Svm.Worker")]
    public async Task HostStartsWithoutApplyingMigrationsAndRejectsMissingConfiguration(string hostName)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The dedicated local PostgreSQL helper requires macOS or Linux.");
        var database = new PersistenceDatabase();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "build/postgres.local.json"))) root = root.Parent;
        Assert.NotNull(root);
        var configPath = Path.Combine(root.FullName, ".cache", $"persistence-host-{Guid.NewGuid():N}.json");
        try
        {
            await database.CreateAsync(migrate: false);
            await using (var file = new FileStream(configPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
                await JsonSerializer.SerializeAsync(file, new { writerConnectionString = database.WriterConnection, readerConnectionString = database.ReaderConnection });
            var info = new ProcessStartInfo(Path.Combine(root.FullName, "eng/dotnet"))
            {
                WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            info.ArgumentList.Add(Path.Combine(root.FullName, "src/hosts", hostName, "bin/Debug/net8.0", hostName + ".dll"));
            info.Environment["SVM_PERSISTENCE_CONFIG_FILE"] = configPath;
            info.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            info.Environment["Logging__LogLevel__Default"] = "Information";
            using (var process = new Process { StartInfo = info })
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                process.OutputDataReceived += (_, e) => { if (e.Data?.Contains("Application started.", StringComparison.Ordinal) == true) ready.TrySetResult(); };
                Assert.True(process.Start());
                process.BeginOutputReadLine();
                var errorOutput = process.StandardError.ReadToEndAsync();
                try
                {
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.False(process.HasExited);
                    Assert.Empty((await database.Runner.StatusAsync(default)).Applied);
                    Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(database.MigrationConnection,
                        "SELECT count(*) FROM pg_namespace WHERE nspname IN ('iam','rel','pkg','ins','tsk','aud','framework')"));
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                Assert.Empty(await errorOutput);
            }
            info.Environment.Remove("SVM_PERSISTENCE_CONFIG_FILE");
            using var missing = Process.Start(info)!;
            var stdout = missing.StandardOutput.ReadToEndAsync();
            var stderr = missing.StandardError.ReadToEndAsync();
            try { await missing.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { if (!missing.HasExited) { missing.Kill(entireProcessTree: true); await missing.WaitForExitAsync(); } }
            Assert.NotEqual(0, missing.ExitCode);
            Assert.Contains("ConfigurationInvalid", await stderr);
            Assert.DoesNotContain("Application started.", await stdout);
        }
        finally
        {
            File.Delete(configPath);
            await database.DisposeAsync();
        }
    }
}

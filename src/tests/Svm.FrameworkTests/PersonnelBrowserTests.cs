using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class PersonnelBrowserTests
{
    [Fact]
    public async Task SameOriginWebManagesAccountsAndManuallyVerifiesALostResponse()
    {
        if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Local browser verification requires the SVM macOS/Linux toolchain.");
        await using var fixture = await PersonnelDatabase.CreateAsync();
        await using var api = await LocalApi.StartAsync(fixture.Database);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "build/postgres.local.json"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("SVM workspace missing.");
        var run = Guid.NewGuid().ToString("N");
        var config = Path.Combine(root, ".cache", $"personnel-browser-{run}.json");
        var artifacts = Path.Combine(root, "artifacts", "personnel", "browser-" + run);
        var passwords = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        var info = new ProcessStartInfo(Path.Combine(root, ".tools/node/bin/node"))
        {
            WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(Path.Combine(root, "src/ui/svm-web/tests/browser/personnel.mjs"));
        info.Environment["SVM_PERSONNEL_BROWSER_CONFIG_FILE"] = config;
        info.Environment["PLAYWRIGHT_BROWSERS_PATH"] = Path.Combine(root, ".cache/playwright-browsers");
        using var process = new Process { StartInfo = info };
        var started = false;
        try
        {
            await using (var file = new FileStream(config, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
                await JsonSerializer.SerializeAsync(file, new
                {
                    url = api.Url, employeeNo = PersonnelDatabase.EmployeeNo, initialPassword = fixture.Password,
                    adminPassword = passwords[0], temporaryPassword = passwords[1], resetPassword = passwords[2],
                    userPassword = passwords[3], artifacts
                });
            started = process.Start();
            Assert.True(started);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout + await stderr;
            foreach (var secret in passwords.Append(fixture.Password))
            {
                PersistenceDatabase.AssertRedacted(output, secret);
                api.AssertRedacted(secret);
            }
            Assert.True(process.ExitCode == 0, output);
            Assert.Contains("Browser personnel verification passed", output);
            Assert.Equal(2, await fixture.CountAsync("iam.users"));
            Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(fixture.Database.ReaderConnection,
                "SELECT count(*) FROM aud.events WHERE \"Operation\"='identity.users.create'"));
            Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(fixture.Database.ReaderConnection,
                "SELECT count(*) FROM iam.operation_results WHERE \"Operation\"='identity.users.create'"));
        }
        finally
        {
            if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            File.Delete(config);
        }
    }
}

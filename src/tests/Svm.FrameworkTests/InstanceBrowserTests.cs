using System.Diagnostics;
using System.Text.Json;
using Svm.Services.Contracts.Catalog;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class InstanceBrowserTests
{
    [Fact]
    public async Task RealHttpsBrowserDisplaysRegisteredFactsAndInstallationHistory()
    {
        if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Local browser verification requires the SVM macOS/Linux toolchain.");
        await using var db = await PersonnelDatabase.CreateAsync();
        await using var api = await LocalApi.StartAsync(db.Database, site: new SiteCatalogOptions(Guid.NewGuid(), "实例浏览器夹具厂区", "Asia/Shanghai"), instanceAccess: InstanceFixture.Limits);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "build/postgres.local.json"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("SVM workspace missing.");
        var run = Guid.NewGuid().ToString("N"); var config = Path.Combine(root, ".cache", $"instance-browser-{run}.json"); var password = Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(Path.Combine(root, ".tools/node/bin/node")) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(root, "src/ui/svm-web/tests/browser/instances.mjs"));
        info.Environment["SVM_INSTANCE_BROWSER_CONFIG_FILE"] = config; info.Environment["PLAYWRIGHT_BROWSERS_PATH"] = Path.Combine(root, ".cache/playwright-browsers");
        using var process = new Process { StartInfo = info }; var started = false;
        try
        {
            await using (var file = new FileStream(config, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
                await JsonSerializer.SerializeAsync(file, new { url = api.Url, employeeNo = PersonnelDatabase.EmployeeNo, initialPassword = db.Password, password, artifacts = Path.Combine(root, "artifacts", "instance-access", "browser-" + run) });
            started = process.Start(); Assert.True(started); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); await process.WaitForExitAsync(timeout.Token); var output = await stdout + await stderr;
            foreach (var secret in new[] { password, db.Password }) { PersistenceDatabase.AssertRedacted(output, secret); api.AssertRedacted(secret); }
            Assert.True(process.ExitCode == 0, output); Assert.Contains("Browser instance verification passed", output);
            Assert.Equal(1, await db.CountAsync("ins.instances")); Assert.Equal(1, await db.CountAsync("ins.installation_evidence"));
            Assert.Equal(1, await db.CountAsync("iam.instance_credentials")); Assert.Equal(1, await db.CountAsync("iam.enrollment_grants"));
        }
        finally { if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } File.Delete(config); }
    }
}

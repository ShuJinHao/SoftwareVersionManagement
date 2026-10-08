using System.Diagnostics;
using System.Text.Json;
using Xunit;
namespace Svm.FrameworkTests;
[Trait("Category", "Business")]
public sealed class PackageBrowserTests
{
    [Fact] public async Task RealHttpsBrowserHashesUploadsDownloadsAndDisplaysLinkedInstallation()
    {
        await using var f = await PackageHostFixture.CreateAsync(); await f.GatewayAsync(); await f.WorkerAsync(f.A);
        var run = Guid.NewGuid().ToString("N"); var password = Guid.NewGuid().ToString("N");
        var config = await OutboxFixture.PrivateJsonAsync(new { url = f.GatewayUrl, employeeNo = PersonnelDatabase.EmployeeNo, initialPassword = f.Personnel.Password, password, artifacts = Path.Combine(OutboxFixture.Root, "artifacts/releases-packages/browser-" + run) });
        var info = new ProcessStartInfo(Path.Combine(OutboxFixture.Root, ".tools/node/bin/node")) { WorkingDirectory = OutboxFixture.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(OutboxFixture.Root, "src/ui/svm-web/tests/browser/packages.mjs")); info.Environment["SVM_PACKAGE_BROWSER_CONFIG_FILE"] = config; info.Environment["PLAYWRIGHT_BROWSERS_PATH"] = Path.Combine(OutboxFixture.Root, ".cache/playwright-browsers");
        using var process = new Process { StartInfo = info }; var started = false;
        try
        {
            started = process.Start(); Assert.True(started); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4)); var output = await stdout + await stderr;
            foreach (var secret in new[] { password, f.Personnel.Password }) { PersistenceDatabase.AssertRedacted(output, secret); f.ApiA.AssertRedacted(secret); f.ApiB.AssertRedacted(secret); }
            Assert.True(process.ExitCode == 0, output); Assert.Contains("Browser package verification passed", output);
            Assert.Equal(1, await f.Personnel.CountAsync("rel.releases")); Assert.Equal(1, await f.Personnel.CountAsync("pkg.packages")); Assert.Equal(1, await f.Personnel.CountAsync("ins.installation_evidence"));
        }
        finally { if (started && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } File.Delete(config); }
    }
}

using System.Diagnostics;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class ReleasePublicationBrowserTests
{
    [Fact]
    public async Task RealHttpsBrowserPublishesInstalledTestDownloadsFormalAndKeepsDisabledHistory()
    {
        await using var f = await PackageHostFixture.CreateAsync(); await f.GatewayAsync(); await f.WorkerAsync(f.A);
        var run = Guid.NewGuid().ToString("N"); var password = Guid.NewGuid().ToString("N");
        var config = await OutboxFixture.PrivateJsonAsync(new { url = f.GatewayUrl, employeeNo = PersonnelDatabase.EmployeeNo, initialPassword = f.Personnel.Password, password, artifacts = Path.Combine(OutboxFixture.Root, "artifacts/release-publication/browser-" + run) });
        var info = new ProcessStartInfo(Path.Combine(OutboxFixture.Root, ".tools/node/bin/node")) { WorkingDirectory = OutboxFixture.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(OutboxFixture.Root, "src/ui/svm-web/tests/browser/publication.mjs")); info.Environment["SVM_PUBLICATION_BROWSER_CONFIG_FILE"] = config; info.Environment["PLAYWRIGHT_BROWSERS_PATH"] = Path.Combine(OutboxFixture.Root, ".cache/playwright-browsers");
        using var process = new Process { StartInfo = info }; var started = false;
        try
        {
            started = process.Start(); Assert.True(started); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4)); var output = await stdout + await stderr;
            foreach (var secret in new[] { password, f.Personnel.Password }) { PersistenceDatabase.AssertRedacted(output, secret); f.ApiA.AssertRedacted(secret); f.ApiB.AssertRedacted(secret); }
            Assert.True(process.ExitCode == 0, output); Assert.Contains("Browser publication verification passed", output);
            Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection, "SELECT count(*) FROM rel.releases WHERE \"State\"='Disabled' AND \"PublishedAt\" IS NOT NULL"));
            Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection, "SELECT count(*) FROM aud.events WHERE \"Operation\"='rel.release.publish'"));
        }
        finally { if (started && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } File.Delete(config); }
    }
}

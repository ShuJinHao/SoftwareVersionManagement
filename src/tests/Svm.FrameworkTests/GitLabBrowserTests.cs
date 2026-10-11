using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Releases;
using Xunit;
using static Svm.FrameworkTests.PackageHttpTests;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class GitLabBrowserTests
{
    [Fact] public async Task HttpsImportInterruptedDownloadTwoWorkersFormalRepairAndDeviceUpdate()
    {
        await using var f = await PackageHostFixture.CreateAsync(tasks: true);
        await using var gitLab = await GitLabProtocolFixture.StartAsync(); using var client = f.Client(new CookieContainer());
        var setup = await Setup(f, client); var software = setup.Software.Id;
        var session = await Get<JsonElement>(client, f.ApiA.Url + "/api/v1/session"); var user = await Get<UserView>(client, f.ApiA.Url + "/api/v1/manage/users/" + session.GetProperty("subjectId").GetGuid());
        await Write<UserView>(client, f.ApiA.Url, setup.Csrf, $"subjects/{user.Id}/permissions", new { expectedRevision = user.Revision, reason = "GitLab 更新夹具显式授权", permissions = user.Permissions.Concat(new[] { "deployment.create", "deployment.control" }.Select(operation => new PermissionView(software, operation))) }, System.Net.Http.HttpMethod.Put);
        await f.ConfigureGitLabAsync(gitLab.Config(software)); await f.GatewayAsync(); gitLab.InterruptOnce = true;
        var work = await Write<ReleaseSyncWorkView>(client, f.ApiA.Url, setup.Csrf, $"software/{software}/release-sync", new { tag = "v2.0.0" });
        var a = await f.WorkerAsync(f.A); var b = await f.WorkerAsync(f.B);
        async Task<ReleaseSyncWorkView> Current() => await Get<ReleaseSyncWorkView>(client, f.ApiA.Url + "/api/v1/manage/release-sync-work/" + work.Id);
        await OutboxFixture.WaitAsync(async () => (await Current()).LastErrorCode is "IMPORT_INTERRUPTED" or "CHECKSUM_MISMATCH", 60);
        await a.DisposeAsync(); await b.DisposeAsync(); Assert.Equal(0, await f.Personnel.CountAsync("pkg.dispatches"));
        var repeat = await Write<ReleaseSyncWorkView>(client, f.ApiA.Url, setup.Csrf, $"software/{software}/release-sync", new { tag = "v2.0.0" }); Assert.Equal(work.Id, repeat.Id);
        await f.WorkerAsync(f.A); await f.WorkerAsync(f.B);
        await OutboxFixture.WaitAsync(async () => (await Current()).Stage == "Completed", 90);
        var release = await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + (await Current()).ReleaseId);
        Assert.Equal("Formal", release.State); Assert.Null(release.TestEvidenceId); Assert.Equal(gitLab.Hash, release.Source!.Manifest.Sha256);
        Assert.Null((await Get<ReleaseSourceView>(client, f.ApiA.Url + $"/api/v1/manage/software/{software}/release-source")).LastErrorCode);
        Assert.Equal(gitLab.Bytes, await File.ReadAllBytesAsync(f.Replica(f.A, release.PackageId))); Assert.Equal(gitLab.Bytes, await File.ReadAllBytesAsync(f.Replica(f.B, release.PackageId)));
        File.Delete(f.Replica(f.B, release.PackageId));
        await OutboxFixture.WaitAsync(() => Task.FromResult(File.Exists(f.Replica(f.B, release.PackageId))), 60);
        Assert.Equal(release.PublishedAt, (await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + release.Id)).PublishedAt);
        Assert.Equal("Formal", (await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + release.Id)).State);
        var run = Guid.NewGuid().ToString("N"); var config = await OutboxFixture.PrivateJsonAsync(new { url = f.GatewayUrl, softwareId = software, releaseId = release.Id,
            sha256 = gitLab.Hash, employeeNo = PersonnelDatabase.EmployeeNo, password = setup.Password, artifacts = Path.Combine(OutboxFixture.Root, "artifacts/gitlab-import/browser-" + run) });
        var info = new ProcessStartInfo(Path.Combine(OutboxFixture.Root, ".tools/node/bin/node")) { WorkingDirectory = OutboxFixture.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(OutboxFixture.Root, "src/ui/svm-web/tests/browser/gitlab.mjs")); info.Environment["SVM_GITLAB_BROWSER_CONFIG_FILE"] = config;
        info.Environment["PLAYWRIGHT_BROWSERS_PATH"] = Path.Combine(OutboxFixture.Root, ".cache/playwright-browsers"); using var process = new Process { StartInfo = info }; var started = false;
        try
        {
            Assert.True(started = process.Start()); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); var output = await stdout + await stderr;
            foreach (var secret in new[] { setup.Password, gitLab.ApiToken, gitLab.PackageToken }) { PersistenceDatabase.AssertRedacted(output, secret); f.ApiA.AssertRedacted(secret); f.ApiB.AssertRedacted(secret); }
            Assert.True(process.ExitCode == 0, output); Assert.Contains("Browser GitLab verification passed", output);
        }
        finally { if (started && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } File.Delete(config); }
        Assert.Equal(1, await f.Personnel.CountAsync("rel.releases"));
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection, "SELECT count(*) FROM ins.installation_evidence WHERE \"InstallationState\"='Installed'"));
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection, "SELECT count(*) FROM ins.installation_evidence WHERE \"InstallationState\"='NotInstalled'"));
        Assert.False(gitLab.CredentialLeak);
    }
}

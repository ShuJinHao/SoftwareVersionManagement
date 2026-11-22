using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Registration;
using Xunit;

namespace Svm.SecurityTests;

[Trait("Category", "Security")]
public sealed class ScaffoldExposureTests
{
    [Fact]
    public async Task OnlyApprovedPersonnelAndCatalogEndpointsAreExposedAndManageRequiresAuthentication()
    {
        await using var host = new WebApplicationFactory<HttpApi.Program>();
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var endpoints = host.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints);
        Assert.Equal(new[]
        {
            // Sessions & Authorization
            "/api/v1/session", "/api/v1/session", "/api/v1/session",
            "/api/v1/session/authorization", "/api/v1/session/password",
            // Users & Roles
            "/api/v1/manage/users", "/api/v1/manage/users",
            "/api/v1/manage/users/{userId:guid}", "/api/v1/manage/users/{userId:guid}",
            "/api/v1/manage/users/{userId:guid}/reset-password",
            "/api/v1/manage/subjects/{subjectId:guid}/permissions",
            "/api/v1/manage/subjects/{subjectId:guid}/authorization", "/api/v1/manage/subjects/{subjectId:guid}/authorization",
            "/api/v1/manage/roles", "/api/v1/manage/roles",
            "/api/v1/manage/roles/{roleId:guid}", "/api/v1/manage/roles/{roleId:guid}",
            // Site Catalog & Hardware Workspace
            "/api/v1/manage/site", "/api/v1/manage/permission-options",
            "/api/v1/manage/processes", "/api/v1/manage/processes",
            "/api/v1/manage/processes/{processId:guid}", "/api/v1/manage/processes/{processId:guid}",
            "/api/v1/manage/devices", "/api/v1/manage/devices",
            "/api/v1/manage/devices/{deviceId:guid}", "/api/v1/manage/devices/{deviceId:guid}",
            "/api/v1/manage/devices/{deviceId:guid}/software-bindings", "/api/v1/manage/devices/{deviceId:guid}/software-bindings",
            "/api/v1/manage/devices/{deviceId:guid}/software-bindings/{softwareId:guid}",
            "/api/v1/manage/devices/{deviceId:guid}/software-inventory",
            "/api/v1/manage/devices/{deviceId:guid}/hardware", "/api/v1/manage/devices/{deviceId:guid}/hardware",
            "/api/v1/manage/hardware/{hardwareId:guid}", "/api/v1/manage/hardware/{hardwareId:guid}",
            "/api/v1/manage/hardware/{hardwareId:guid}/software-bindings", "/api/v1/manage/hardware/{hardwareId:guid}/software-bindings",
            "/api/v1/manage/hardware/{hardwareId:guid}/software-bindings/{softwareId:guid}",
            "/api/v1/manage/instances/{instanceId:guid}/hardware-binding",
            "/api/v1/manage/workspace/devices", "/api/v1/manage/workspace/installations",
            "/api/v1/manage/software", "/api/v1/manage/software",
            "/api/v1/manage/software/{softwareId:guid}", "/api/v1/manage/software/{softwareId:guid}",
            // Instances & Enrollment / Recovery
            "/api/v1/manage/enrollment-grants", "/api/v1/manage/enrollment-grants",
            "/api/v1/manage/enrollment-grants/{grantId:guid}/revoke",
            "/api/v1/manage/instances", "/api/v1/manage/instances/{instanceId:guid}",
            "/api/v1/manage/instances/{instanceId:guid}/credentials",
            "/api/v1/manage/instances/{instanceId:guid}/version-history",
            "/api/v1/manage/instances/{instanceId:guid}/lifecycle",
            "/api/v1/manage/instances/{instanceId:guid}/recovery-grants",
            "/api/v1/manage/credentials/{credentialId:guid}/revoke",
            "/api/v1/manage/recovery-grants/{grantId:guid}/revoke",
            "/api/v1/enrollment/instances", "/api/v1/enrollment/recoveries",
            "/api/v1/client/context", "/api/v1/client/report-streams", "/api/v1/client/status-reports",
            // Releases, Packages & Cleanup
            "/api/v1/manage/capabilities", "/api/v1/manage/audit-events",
            "/api/v1/manage/software/{softwareId:guid}/releases", "/api/v1/manage/software/{softwareId:guid}/releases",
            "/api/v1/manage/releases/{releaseId:guid}",
            "/api/v1/manage/releases/{releaseId:guid}/disable",
            "/api/v1/manage/releases/{releaseId:guid}/publish",
            "/api/v1/manage/releases/{releaseId:guid}/test-evidence",
            "/api/v1/manage/uploads/{uploadId:guid}", "/api/v1/manage/uploads/{uploadId:guid}/content",
            "/api/v1/manage/packages/{packageId:guid}",
            "/api/v1/manage/packages/{packageId:guid}/retry",
            "/api/v1/manage/packages/{packageId:guid}/cleanup", "/api/v1/manage/packages/{packageId:guid}/cleanup",
            "/api/v1/manage/packages/{packageId:guid}/download-stop-evidence",
            "/api/v1/manage/package-work/{workId:guid}",
            "/api/v1/client/versions", "/api/v1/client/versions/{releaseId:guid}",
            "/api/v1/client/packages/{packageId:guid}",
            // GitLab Source Management
            "/api/v1/manage/software/{softwareId:guid}/release-source", "/api/v1/manage/software/{softwareId:guid}/release-source",
            "/api/v1/manage/software/{softwareId:guid}/release-source-options",
            "/api/v1/manage/software/{softwareId:guid}/release-sync",
            "/api/v1/manage/release-sync-work/{workId:guid}",
            // PLC Archives & Usage
            "/api/v1/manage/software/{softwareId:guid}/plc-upload-credentials", "/api/v1/manage/software/{softwareId:guid}/plc-upload-credentials",
            "/api/v1/manage/plc-upload-credentials/{credentialId:guid}/revoke",
            "/api/v1/manage/releases/{releaseId:guid}/plc-publish",
            "/api/v1/manage/releases/{releaseId:guid}/plc-evidence",
            "/api/v1/manage/hardware/{hardwareId:guid}/plc-usage",
            "/api/v1/manage/hardware-bindings/{bindingId:guid}/plc-usage", "/api/v1/manage/hardware-bindings/{bindingId:guid}/plc-usage",
            "/api/v1/plc-upload/archives",
            "/api/v1/plc-upload/uploads/{uploadId:guid}", "/api/v1/plc-upload/uploads/{uploadId:guid}/content",
            "/api/v1/plc-upload/package-work/{workId:guid}",
            "/api/v1/plc-upload/packages/{packageId:guid}/retry",
            // Deployments, Tasks & Compatibility Declarations
            "/api/v1/manage/software/{softwareId:guid}/deployment-capabilities",
            "/api/v1/manage/target-selections",
            "/api/v1/manage/target-selections/{selectionId:guid}",
            "/api/v1/manage/target-selections/{selectionId:guid}/chunks/{chunkNo:int}",
            "/api/v1/manage/target-selections/{selectionId:guid}/seal",
            "/api/v1/manage/deployments", "/api/v1/manage/deployments",
            "/api/v1/manage/deployments/{deploymentId:guid}",
            "/api/v1/manage/deployments/{deploymentId:guid}/targets",
            "/api/v1/manage/deployments/{deploymentId:guid}/batches",
            "/api/v1/manage/deployments/{deploymentId:guid}/tasks",
            "/api/v1/manage/deployments/{deploymentId:guid}/pause",
            "/api/v1/manage/deployments/{deploymentId:guid}/resume",
            "/api/v1/manage/deployments/{deploymentId:guid}/reschedule",
            "/api/v1/manage/deployments/{deploymentId:guid}/cancel",
            "/api/v1/manage/tasks/{taskId:guid}",
            "/api/v1/manage/tasks/{taskId:guid}/receipts",
            "/api/v1/manage/tasks/{taskId:guid}/defer",
            "/api/v1/manage/tasks/{taskId:guid}/restore",
            "/api/v1/manage/tasks/{taskId:guid}/cancel",
            "/api/v1/manage/tasks/{taskId:guid}/close-unknown",
            "/api/v1/manage/deployment-work/{workId:guid}",
            "/api/v1/manage/deployment-work/{workId:guid}/items",
            "/api/v1/manage/releases/{releaseId:guid}/integration-materials", "/api/v1/manage/releases/{releaseId:guid}/integration-materials",
            "/api/v1/manage/releases/{releaseId:guid}/compatibility-declarations", "/api/v1/manage/releases/{releaseId:guid}/compatibility-declarations",
            "/api/v1/client/tasks", "/api/v1/client/tasks/{taskId:guid}",
            "/api/v1/client/tasks/{taskId:guid}/claim",
            "/api/v1/client/attempts/{attemptId:guid}/start",
            "/api/v1/client/attempts/{attemptId:guid}/receipts"
        }.Order(),
            endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).Where(p => p is not null && p.StartsWith("/api/v1/", StringComparison.Ordinal)).Order().ToArray());
        // The production composition does not discover test identities or test requests.
        using (var scope = host.Services.CreateScope())
        {
            Assert.NotNull(scope.ServiceProvider.GetService<IRequestAuthorizer>());
            Assert.NotNull(scope.ServiceProvider.GetService<ITrustedCallContextSource>());
        }
        var error = Assert.Throws<RequestRejectedException>(() => host.Services.GetRequiredService<RequestCatalog>()
            .GetPolicy(typeof(RequestPipelineTests.ManageQuery)));
        Assert.Equal("CONFIGURATION_INVALID", error.Code);
        using var response = await client.GetAsync("/api/v1/unknown");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        using var manage = await client.GetAsync("/api/v1/manage/users");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, manage.StatusCode);
        foreach (var path in new[] { "site", "permission-options", "processes", "devices", "software", "roles", "workspace/devices", "instances?softwareId=" + Guid.NewGuid() })
        {
            using var catalogResponse = await client.GetAsync("/api/v1/manage/" + path);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, catalogResponse.StatusCode);
        }
        using var insecure = host.CreateClient();
        using var plaintext = await insecure.GetAsync("/api/v1/manage/users");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, plaintext.StatusCode);
    }
}

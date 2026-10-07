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
        Assert.Equal(new[] { "/api/v1/session", "/api/v1/session", "/api/v1/session", "/api/v1/session/password",
            "/api/v1/manage/users", "/api/v1/manage/users", "/api/v1/manage/users/{userId:guid}", "/api/v1/manage/users/{userId:guid}",
            "/api/v1/manage/users/{userId:guid}/reset-password", "/api/v1/manage/subjects/{subjectId:guid}/permissions",
            "/api/v1/manage/site", "/api/v1/manage/permission-options", "/api/v1/manage/processes", "/api/v1/manage/processes",
            "/api/v1/manage/processes/{processId:guid}", "/api/v1/manage/processes/{processId:guid}", "/api/v1/manage/devices", "/api/v1/manage/devices",
            "/api/v1/manage/devices/{deviceId:guid}", "/api/v1/manage/devices/{deviceId:guid}", "/api/v1/manage/software", "/api/v1/manage/software",
            "/api/v1/manage/software/{softwareId:guid}", "/api/v1/manage/software/{softwareId:guid}",
            "/api/v1/manage/devices/{deviceId:guid}/software-bindings", "/api/v1/manage/devices/{deviceId:guid}/software-bindings",
            "/api/v1/manage/devices/{deviceId:guid}/software-bindings/{softwareId:guid}", "/api/v1/manage/devices/{deviceId:guid}/software-inventory" }.Order(),
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
        foreach (var path in new[] { "site", "permission-options", "processes", "devices", "software" })
        {
            using var catalogResponse = await client.GetAsync("/api/v1/manage/" + path);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, catalogResponse.StatusCode);
        }
        using var insecure = host.CreateClient();
        using var plaintext = await insecure.GetAsync("/api/v1/manage/users");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, plaintext.StatusCode);
    }
}

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
    public async Task ScaffoldDoesNotExposeAnUnauthenticatedBusinessOrSwaggerEndpoint()
    {
        await using var host = new WebApplicationFactory<HttpApi.Program>();
        using var client = host.CreateClient();
        var endpoints = host.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints);
        Assert.Equal(new[] { "/api/v1/session", "/api/v1/session", "/api/v1/session", "/api/v1/session/password" },
            endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).Order().ToArray());
        // The production composition does not discover test identities or test requests.
        using (var scope = host.Services.CreateScope())
        {
            Assert.NotNull(scope.ServiceProvider.GetService<IRequestAuthorizer>());
            Assert.NotNull(scope.ServiceProvider.GetService<ITrustedCallContextSource>());
        }
        var error = Assert.Throws<RequestRejectedException>(() => host.Services.GetRequiredService<RequestCatalog>()
            .GetPolicy(typeof(RequestPipelineTests.ManageQuery)));
        Assert.Equal("CONFIGURATION_INVALID", error.Code);
        using var response = await client.GetAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}

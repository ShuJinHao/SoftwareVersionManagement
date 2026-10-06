using System.Text.Json;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class HostRuntimeTests
{
    [Fact]
    public void BuiltHttpHostRequiresThePinnedRuntime()
    {
        var assembly = typeof(HttpApi.Program).Assembly.Location;
        var config = Path.ChangeExtension(assembly, ".runtimeconfig.json");
        using var document = JsonDocument.Parse(File.ReadAllText(config));
        var options = document.RootElement.GetProperty("runtimeOptions");
        Assert.Equal("Disable", options.GetProperty("rollForward").GetString());
        var frameworks = options.GetProperty("frameworks").EnumerateArray().ToArray();
        Assert.Contains(frameworks, f => f.GetProperty("name").GetString() == "Microsoft.NETCore.App" && f.GetProperty("version").GetString() == "8.0.31");
        Assert.Contains(frameworks, f => f.GetProperty("name").GetString() == "Microsoft.AspNetCore.App" && f.GetProperty("version").GetString() == "8.0.31");
    }
}

using System.Runtime.InteropServices;
using System.Text.Json;

namespace Svm.FrameworkTests;

internal static class RepositoryTools
{
    private static readonly string Platform = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "darwin" : "linux")
        + "-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");
    private static string Version(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(OutboxFixture.Root,
        "build", "toolchain.lock.json"))).RootElement.GetProperty(name).GetString()!;
    internal static string Node => Environment.GetEnvironmentVariable("SVM_NODE") ?? Path.Combine(OutboxFixture.Root,
        ".tools", Platform, "node", Version("node"), OperatingSystem.IsWindows() ? "node.exe" : "bin/node");
    internal static string Dotnet => Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.Combine(OutboxFixture.Root,
        ".tools", Platform, "dotnet", Version("dotnetSdk")), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
}

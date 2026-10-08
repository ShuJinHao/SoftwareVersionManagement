using Svm.Application;
using Svm.Core.Releases;
using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Xunit;
namespace Svm.FrameworkTests;
[Trait("Category", "Business")]
public sealed class PackageCompositionTests
{
    [Fact] public void OnlyOneRealConsumerIsRegisteredAndNoTaskOrFormalPublishCommandIsOpened()
    { Assert.Single(ApplicationRegistration.PackageConsumers); Assert.Equal("PackageWorkAvailableV1", ApplicationRegistration.PackageConsumers[0].EventType.Name); Assert.DoesNotContain(PackageCapabilities.Writes, x => x.Name.Contains("Publish", StringComparison.Ordinal) || x.Name.Contains("Task", StringComparison.Ordinal) || x.Name.Contains("Cleanup", StringComparison.Ordinal)); }
    [Theory][InlineData("Patch", 3, 5, 10)][InlineData("Minor", 3, 6, 0)][InlineData("Major", 4, 0, 0)]
    public void NextNumbersAreSemanticAndFirstVersionAlwaysStartsAtOne(string level, int a, int b, int c)
    { Assert.Equal((1, 0, 0), SoftwareRelease.Next(null, level)); var prior = new SoftwareRelease(Guid.NewGuid(), Guid.NewGuid(), 3, 5, 9, "Patch", "fixture", "fixture", Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow); Assert.Equal((a, b, c), SoftwareRelease.Next(prior, level)); }
    [Theory][InlineData(0, 5)][InlineData(1, 4)][InlineData(1099511627777, 5)][InlineData(1, 601)]
    public void ExplicitPackageLimitsRejectUnboundedValues(long size, int idle) => Assert.Equal(RequestFailure.ConfigurationInvalid, Assert.Throws<RequestRejectedException>(() => new PackageLimits(size, idle).Validate()).Failure);
    [Fact] public void MissingFileConfigurationDisablesCapabilitiesAndMalformedPrivateConfigNeverFallsBack()
    { Assert.Null(PackageFileOptions.Load(null)); var path = Path.Combine(OutboxFixture.Root, ".cache", "package-malformed-" + Guid.NewGuid().ToString("N") + ".json"); try { File.WriteAllText(path, "{\"password\":\"fixture-private-secret\",\"nodeId\":\"../../bad\"}"); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); var e = Assert.Throws<RequestRejectedException>(() => PackageFileOptions.Load(path)); Assert.Equal(RequestFailure.ConfigurationInvalid, e.Failure); PersistenceDatabase.AssertRedacted(e.ToString(), "fixture-private-secret"); } finally { File.Delete(path); } }
}

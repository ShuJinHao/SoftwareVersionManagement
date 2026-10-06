using System.Xml.Linq;
using Svm.Analyzers;
using Xunit;

namespace Svm.ArchitectureTests;

[Trait("Category", "Architecture")]
public sealed class ProjectGraphTests
{
    private static string Root
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null && !File.Exists(Path.Combine(current.FullName, "build", "Architecture.xml"))) current = current.Parent;
            return current?.FullName ?? throw new InvalidOperationException("Cannot find SVM project root.");
        }
    }

    [Fact]
    public void ActualProjectsMatchTheApprovedGraphAndDocument()
    {
        var policy = File.ReadAllText(Path.Combine(Root, "build", "Architecture.xml"));
        var files = Directory.GetFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(p => new KeyValuePair<string, string>(p, File.ReadAllText(p)));
        Assert.Empty(ArchitectureGraph.Validate(Root, policy, files));

        var document = File.ReadAllText(Path.Combine(Root, "docs", "软件框架设计.md"));
        var section = document.Split("### 3.2", StringSplitOptions.None)[1].Split("### 3.3", StringSplitOptions.None)[0];
        var expected = section.Split('\n').Where(l => l.StartsWith("| `Svm.", StringComparison.Ordinal)).ToDictionary(
            l => l.Split('|')[1].Trim().Trim('`'),
            l => l.Split('|')[3].Trim() == "无" ? Array.Empty<string>() : l.Split('|')[3].Trim().Split('、'));
        var projects = XDocument.Parse(policy).Root!.Elements("Project").ToArray();
        Assert.Equal(expected.Keys.Order(), projects.Select(p => (string)p.Attribute("name")!).Order());
        foreach (var project in projects)
        {
            var name = (string)project.Attribute("name")!;
            Assert.Equal(expected[name].Order(), project.Elements("Reference").Select(r => (string)r.Attribute("name")!).Order());
        }
    }

    [Theory]
    [InlineData("Svm.EntityFrameworkCore")]
    [InlineData("Svm.Security")]
    [InlineData("Svm.SecurityTests")]
    [InlineData("Svm.Core.Tasks")]
    public void UnapprovedProjectReferenceIsRejected(string forbidden)
    {
        var policy = $"<Architecture><Project name='Svm.Core.Identity' path='core.csproj'/><Project name='{forbidden}' path='other.csproj'/></Architecture>";
        var files = Inputs(("core.csproj", "<Project><ItemGroup><ProjectReference Include='other.csproj'/></ItemGroup></Project>"), ("other.csproj", "<Project/>"));
        Assert.Contains(ArchitectureGraph.Validate(Root, policy, files), e => e.Contains("forbidden reference to " + forbidden, StringComparison.Ordinal));
    }

    [Fact]
    public void CycleIsRejectedEvenIfItsEdgesAppearInThePolicy()
    {
        const string policy = "<Architecture><Project name='A' path='a.csproj'><Reference name='B'/></Project><Project name='B' path='b.csproj'><Reference name='A'/></Project></Architecture>";
        var files = Inputs(("a.csproj", "<Project><ProjectReference Include='b.csproj'/></Project>"), ("b.csproj", "<Project><ProjectReference Include='a.csproj'/></Project>"));
        Assert.Contains(ArchitectureGraph.Validate(Root, policy, files), e => e.Contains("Circular", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<PackageReference Include='MassTransit'/>", "forbidden direct package")]
    [InlineData("<Reference Include='Dapper'/>", "raw assembly references")]
    [InlineData("<FrameworkReference Include='Microsoft.AspNetCore.App'/>", "unapproved framework")]
    [InlineData("<ProjectReference Include='$(Outside)'/>", "literal unconditional")]
    public void AlternateDependencyPathsAreRejected(string element, string expected)
    {
        const string policy = "<Architecture><Project name='Svm.Core.Identity' path='core.csproj'/></Architecture>";
        Assert.Contains(ArchitectureGraph.Validate(Root, policy, Inputs(("core.csproj", "<Project><ItemGroup>" + element + "</ItemGroup></Project>"))), e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void MissingAndUnlistedProjectsAreRejected()
    {
        const string policy = "<Architecture><Project name='A' path='a.csproj'/></Architecture>";
        var errors = ArchitectureGraph.Validate(Root, policy, Inputs(("b.csproj", "<Project/>")));
        Assert.Contains(errors, e => e.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Unlisted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("VersionOverride")]
    public void DirectPackageVersionCannotOverrideTheCentralBaseline(string attribute)
    {
        const string policy = "<Architecture><Project name='Svm.Application' path='app.csproj'><Package name='MediatR'/></Project></Architecture>";
        var input = $"<Project><ItemGroup><PackageReference Include='MediatR' {attribute}='13.0.0'/></ItemGroup></Project>";
        Assert.Contains(ArchitectureGraph.Validate(Root, policy, Inputs(("app.csproj", input))), e => e.Contains("central baseline", StringComparison.Ordinal));
    }

    private static IEnumerable<KeyValuePair<string, string>> Inputs(params (string Path, string Text)[] files) =>
        files.Select(f => new KeyValuePair<string, string>(Path.Combine(Root, f.Path), f.Text));
}

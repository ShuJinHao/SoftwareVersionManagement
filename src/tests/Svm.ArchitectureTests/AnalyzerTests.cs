using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Svm.Analyzers;
using Xunit;

namespace Svm.ArchitectureTests;

[Trait("Category", "Architecture")]
public sealed class AnalyzerTests
{
    [Fact]
    public async Task ValidCompilationHasNoArchitectureDiagnostics()
    {
        var diagnostics = await Analyze("Svm.Core.Identity", null, WithPolicy());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task MissingPolicyFailsClosed()
    {
        var diagnostics = await Analyze("Svm.Core.Identity", null, ImmutableArray<AdditionalText>.Empty);
        Assert.Contains(diagnostics, d => d.Id == "SVM003" && d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("Svm.Core.Identity", "Microsoft.EntityFrameworkCore")]
    [InlineData("Svm.SharedKernel", "Npgsql")]
    [InlineData("Svm.Services.Contracts", "RabbitMQ.Client")]
    [InlineData("Svm.Application", "MassTransit")]
    [InlineData("Svm.ReleaseService", "Dapper")]
    [InlineData("Svm.SharedKernel", "Svm.Security")]
    [InlineData("Svm.Core.Identity", "Svm.Security")]
    [InlineData("Svm.Services.Contracts", "Svm.Security")]
    [InlineData("Svm.Services.CrossCutting", "Svm.Security")]
    [InlineData("Svm.Application", "Svm.Security")]
    [InlineData("Svm.IdentityService", "Svm.Security")]
    public async Task InnerLayersRejectInfrastructureAssemblyReferences(string owner, string forbidden)
    {
        var diagnostics = await Analyze(owner, forbidden, WithPolicy());
        Assert.Contains(diagnostics, d => d.Id == "SVM002" && d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("Svm.Services.Contracts")]
    [InlineData("Svm.SharedKernel")]
    public async Task SecurityAdapterMayDependOnInnerContracts(string contract)
    {
        var diagnostics = await Analyze("Svm.Security", contract, WithPolicy());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task GraphViolationIsReportedByTheCompilerAnalyzer()
    {
        var diagnostics = await Analyze("Svm.Core.Identity", null, WithPolicy("<Project><PackageReference Include='Dapper'/></Project>"));
        Assert.Contains(diagnostics, d => d.Id == "SVM001");
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string owner, string? forbidden, ImmutableArray<AdditionalText> files)
    {
        var references = new List<MetadataReference> { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        if (forbidden != null)
        {
            var library = CSharpCompilation.Create(forbidden, references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var bytes = new MemoryStream();
            var emitted = library.Emit(bytes);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            references.Add(MetadataReference.CreateFromImage(bytes.ToArray()));
        }
        var compilation = CSharpCompilation.Create(owner, [CSharpSyntaxTree.ParseText("internal class Sample { }")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new LayerAnalyzer()),
            new AnalyzerOptions(files)).GetAnalyzerDiagnosticsAsync();
    }

    private static ImmutableArray<AdditionalText> WithPolicy(string project = "<Project/>")
    {
        var root = Path.Combine(Path.GetTempPath(), "svm-analyzer-inputs");
        return [new TextFile(Path.Combine(root, "build", "Architecture.xml"), "<Architecture><Project name='Svm.Core.Identity' path='core.csproj'/></Architecture>"),
            new TextFile(Path.Combine(root, "core.csproj"), project)];
    }

    private sealed class TextFile(string path, string contents) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(contents);
    }
}

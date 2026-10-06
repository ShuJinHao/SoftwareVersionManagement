using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Svm.Analyzers;
using Xunit;

namespace Svm.ArchitectureTests;

[Trait("Category", "Architecture")]
public sealed class CompositionAnalyzerTests
{
    [Theory]
    [InlineData("Svm.Application", "public class Bad(System.IServiceProvider provider) { public object? Run() => provider.GetService(typeof(string)); }")]
    [InlineData("Svm.ReleaseService", "public class Bad { private static System.IServiceProvider? _provider; }")]
    [InlineData("Svm.Services.CrossCutting", "public class Bad { public System.Func<System.IServiceProvider, object>? Factory { get; set; } }")]
    [InlineData("Svm.Core.Identity", "public class Bad { public object? Run(System.IServiceProvider p) { var get = p.GetService; return get(typeof(string)); } }")]
    public async Task BusinessCannotReceiveStoreOrUseContainer(string assembly, string source)
    {
        var diagnostics = await Analyze(assembly, source);
        Assert.Contains(diagnostics, d => d.Id == "SVM004" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task TrustedActorConstructionIsRejectedInApplication()
    {
        var diagnostics = await Analyze("Svm.Application", """
            namespace Svm.Services.Contracts.Framework { public sealed class CallActor {} }
            namespace App { public class Bad { public object Run() => new Svm.Services.Contracts.Framework.CallActor(); } }
            """);
        Assert.Contains(diagnostics, d => d.Id == "SVM004");
    }

    [Fact]
    public async Task BusinessCannotCaptureTheHostBackgroundDispatcher()
    {
        var diagnostics = await Analyze("Svm.Application", """
            namespace Svm.Services.CrossCutting.Pipeline { public sealed class ScopedRequestExecutor {} }
            public class Bad(Svm.Services.CrossCutting.Pipeline.ScopedRequestExecutor executor) {}
            """);
        Assert.Contains(diagnostics, d => d.Id == "SVM004");
    }

    [Fact]
    public async Task InternalContainerBuildIsRejectedByBoundMethodSymbol()
    {
        var diagnostics = await Analyze("Svm.Application", """
            namespace Microsoft.Extensions.DependencyInjection {
                public static class ServiceCollectionContainerBuilderExtensions { public static object BuildServiceProvider() => new object(); }
            }
            public class Bad { public object Run() => Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(); }
            """);
        Assert.Contains(diagnostics, d => d.Id == "SVM004");
    }

    [Theory]
    [InlineData("Svm.HttpApi")]
    [InlineData("Svm.Worker")]
    [InlineData("Svm.Migration")]
    public async Task HostsAreAllowedToCompose(string assembly)
    {
        Assert.Empty(await Analyze(assembly, "public class Root(System.IServiceProvider provider) { public object? Resolve() => provider.GetService(typeof(string)); }"));
    }

    [Fact]
    public async Task ConstructorInjectionOfABusinessPortIsAllowed()
    {
        Assert.Empty(await Analyze("Svm.ReleaseService", "public interface IReadPort {} public class Service(IReadPort port) { public IReadPort Port => port; }"));
    }

    [Fact]
    public async Task DiagnosticCannotBeDowngradedByCompilationOptions()
    {
        var diagnostics = await Analyze("Svm.Application", "public class Bad(System.IServiceProvider provider) {}", suppress: true);
        Assert.Contains(diagnostics, d => d.Id == "SVM004" && d.Severity == DiagnosticSeverity.Error);
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string assembly, string source, bool suppress = false)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        if (suppress) options = options.WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["SVM004"] = ReportDiagnostic.Suppress });
        var compilation = CSharpCompilation.Create(assembly, [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12))], references, options);
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new CompositionAnalyzer())).GetAnalyzerDiagnosticsAsync();
    }
}

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Svm.Analyzers;
using Xunit;

namespace Svm.ArchitectureTests;

[Trait("Category", "Architecture")]
public sealed class IntegrationOutboxAnalyzerTests
{
    [Theory]
    [InlineData("Svm.IdentityService", true, true)]
    [InlineData("Svm.PackageService", true, true)]
    [InlineData("Svm.Application", false, false)]
    [InlineData("Svm.Application", false, true)]
    [InlineData("Svm.Application", true, false)]
    [InlineData("Svm.Core.Packages", true, true)]
    public async Task ModulesQueriesAndNonAtomicCommandsCannotAcquireTheOutbox(string assembly, bool command, bool atomic)
    { Assert.Contains(await Analyze(assembly, Source(command, atomic)), d => d.Id == "SVM006"); }
    [Fact]
    public async Task AtomicApplicationCommandUsesOnlyTheOwnPort()
    { Assert.Empty(await Analyze("Svm.Application", Source(true, true))); }
    [Fact]
    public async Task DiagnosticCannotBeSuppressed()
    { Assert.Contains(await Analyze("Svm.Application", Source(false, false), true), d => d.Id == "SVM006" && d.Severity == DiagnosticSeverity.Error); }
    [Theory]
    [InlineData("Svm.Application","TaskPreparationAvailableV1",false)]
    [InlineData("Svm.TaskService","TaskPreparationAvailableV1",true)]
    [InlineData("Svm.Application","UnknownV9",true)]
    public async Task OnlyExplicitApplicationIntegrationContractsCanComposeOutgoingIntents(string assembly,string message,bool rejected)
    {
        var source=$$"""
            namespace Svm.Services.Contracts.Framework {
              public interface IIntegrationEventOutbox { void Enqueue(); }
              public interface IIntegrationEventHandler<T> {}
            }
            namespace Svm.Services.Contracts.Messaging.V1 { public class {{message}} {} }
            public sealed class Handler(Svm.Services.Contracts.Framework.IIntegrationEventOutbox outbox) :
                Svm.Services.Contracts.Framework.IIntegrationEventHandler<Svm.Services.Contracts.Messaging.V1.{{message}}> {
              public void Run() => outbox.Enqueue();
            }
            """;
        var diagnostics=await Analyze(assembly,source);
        Assert.Equal(rejected,diagnostics.Any(d=>d.Id=="SVM006"));
    }
    private static string Source(bool command, bool atomic) => $$"""
        namespace Svm.Services.Contracts.Framework {
          public interface IIntegrationEventOutbox { void Enqueue(); }
          public interface ICommand<T> {} public interface IQuery<T> {}
          [System.AttributeUsage(System.AttributeTargets.Class)]
          public class RequestPolicyAttribute(string op, int owner, int kind, int scope, int transaction) : System.Attribute {}
        }
        namespace MediatR { public interface IRequestHandler<T,R> {} }
        [Svm.Services.Contracts.Framework.RequestPolicy("fixture",3,2,1,{{(atomic ? 2 : 1)}})]
        public sealed class Request : Svm.Services.Contracts.Framework.{{(command ? "ICommand" : "IQuery")}}<int> {}
        public sealed class Handler(System.Func<Svm.Services.Contracts.Framework.IIntegrationEventOutbox> outbox) : MediatR.IRequestHandler<Request,int> {
          public void Run() { System.Action call = outbox().Enqueue; call(); }
        }
        """;
    private static async Task<ImmutableArray<Diagnostic>> Analyze(string assembly, string source, bool suppress = false)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        if (suppress) options = options.WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["SVM006"] = ReportDiagnostic.Suppress });
        var compilation = CSharpCompilation.Create(assembly, [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12))], references, options);
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new IntegrationOutboxAnalyzer())).GetAnalyzerDiagnosticsAsync();
    }
}

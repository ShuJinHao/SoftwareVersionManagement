using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Svm.Analyzers;
using Xunit;

namespace Svm.ArchitectureTests;

[Trait("Category", "Architecture")]
public sealed class DomainEventAnalyzerTests
{
    [Theory]
    [InlineData("System.IO.File.WriteAllText(\"a\", \"b\");")]
    [InlineData("System.Action<string, string> save = System.IO.File.WriteAllText; save(\"a\", \"b\");")]
    [InlineData("using var file = new System.IO.FileStream(\"a\", System.IO.FileMode.Open);")]
    [InlineData("new System.IO.FileInfo(\"a\").Delete();")]
    [InlineData("System.Net.Dns.GetHostAddresses(\"example.invalid\");")]
    [InlineData("System.Diagnostics.Process.Start(\"example.invalid\");")]
    [InlineData("using var client = new System.Net.Http.HttpClient(); await client.GetAsync(\"https://example.invalid\", cancellationToken);")]
    [InlineData("using var socket = new System.Net.Sockets.TcpClient();")]
    [InlineData("new MassTransit.Bus().Publish();")]
    [InlineData("new Confluent.Kafka.Producer().Send();")]
    [InlineData("new Svm.Services.Contracts.Instances.DevicePort().Change();")]
    public async Task HandlersRejectExternalEffectsAndOtherModuleRules(string operation)
    {
        Assert.Contains(await Analyze("Svm.IdentityService", Source(operation)), d => d.Id == "SVM005");
    }

    [Theory]
    [InlineData("Svm.Application")]
    [InlineData("Svm.InstanceService")]
    public async Task HandlerOwnershipCannotBeMovedToApplicationOrAnotherModule(string assembly)
    {
        Assert.Contains(await Analyze(assembly, Source("await System.Threading.Tasks.Task.CompletedTask;")), d => d.Id == "SVM005");
    }

    [Theory]
    [InlineData("Svm.Services.Contracts.Framework.IUnitOfWork")]
    [InlineData("Svm.Services.Contracts.Framework.IDomainEventDispatcher")]
    [InlineData("Svm.Services.Contracts.Framework.IIntegrationEventOutbox")]
    [InlineData("Svm.Services.Contracts.Framework.IIntegrationWorkAuthorizer")]
    [InlineData("Svm.Services.Contracts.Framework.IIntegrationEventPreflight")]
    [InlineData("Svm.Services.Contracts.Framework.IIntegrationConsumptionRecovery")]
    [InlineData("Svm.Services.Contracts.Framework.IIntegrationConsumptionTransaction")]
    [InlineData("Svm.Services.Contracts.Framework.IIntegrationEventDispatcher<Svm.Core.Identity.Recorded>")]
    [InlineData("System.Func<System.Net.Http.HttpClient>")]
    [InlineData("Svm.Services.Contracts.Instances.DevicePort[]")]
    public async Task ConstructorInjectionDoesNotHideForbiddenEffects(string port)
    {
        var source = Source("await System.Threading.Tasks.Task.CompletedTask;", $"({port} port)");
        Assert.Contains(await Analyze("Svm.IdentityService", source), d => d.Id == "SVM005");
    }

    [Fact]
    public async Task ScopedModulePortAndPureRulesAreAllowed()
    {
        Assert.Empty(await Analyze("Svm.IdentityService", Source("port.Apply(domainEvent); await System.Threading.Tasks.Task.CompletedTask;",
            "(Svm.Services.Contracts.Identity.AccountPort port)")));
    }

    [Fact]
    public async Task OwnershipDiagnosticCannotBeSuppressed()
    {
        Assert.Contains(await Analyze("Svm.Application", Source("await System.Threading.Tasks.Task.CompletedTask;"), suppress: true),
            d => d.Id == "SVM005" && d.Severity == DiagnosticSeverity.Error);
    }

    private static string Source(string operation, string constructor = "") => $$"""
        namespace Svm.Services.Contracts.Framework {
            public interface IDomainEventHandler<T> { System.Threading.Tasks.Task HandleAsync(T domainEvent, System.Threading.CancellationToken cancellationToken); }
            public interface IUnitOfWork {}
            public interface IDomainEventDispatcher {}
            public interface IIntegrationEventOutbox {}
            public interface IIntegrationWorkAuthorizer {}
            public interface IIntegrationEventPreflight {}
            public interface IIntegrationConsumptionRecovery {}
            public interface IIntegrationConsumptionTransaction {}
            public interface IIntegrationEventDispatcher<T> {}
        }
        namespace Svm.Services.Contracts.Identity { public class AccountPort { public void Apply(Svm.Core.Identity.Recorded e) {} } }
        namespace Svm.Services.Contracts.Instances { public class DevicePort { public void Change() {} } }
        namespace MassTransit { public class Bus { public void Publish() {} } }
        namespace Confluent.Kafka { public class Producer { public void Send() {} } }
        public sealed class Handler{{constructor}} : Svm.Services.Contracts.Framework.IDomainEventHandler<Svm.Core.Identity.Recorded> {
            public async System.Threading.Tasks.Task HandleAsync(Svm.Core.Identity.Recorded domainEvent, System.Threading.CancellationToken cancellationToken) {
                {{operation}}
            }
        }
        """;

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string assembly, string source, bool suppress = false)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToList();
        var core = CSharpCompilation.Create("Svm.Core.Identity", [CSharpSyntaxTree.ParseText("namespace Svm.Core.Identity { public sealed class Recorded {} }")],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        Assert.True(core.Emit(bytes).Success);
        references.Add(MetadataReference.CreateFromImage(bytes.ToArray()));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        if (suppress) options = options.WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["SVM005"] = ReportDiagnostic.Suppress });
        var compilation = CSharpCompilation.Create(assembly, [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12))], references, options);
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new DomainEventAnalyzer())).GetAnalyzerDiagnosticsAsync();
    }
}

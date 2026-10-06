using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Svm.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LayerAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor GraphRule = new DiagnosticDescriptor(
        "SVM001", "Project graph violates the approved architecture", "{0}",
        "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd, WellKnownDiagnosticTags.NotConfigurable });
    private static readonly DiagnosticDescriptor LayerRule = new DiagnosticDescriptor(
        "SVM002", "Infrastructure dependency in an inner layer", "{0} must not reference {1}",
        "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd, WellKnownDiagnosticTags.NotConfigurable });
    private static readonly DiagnosticDescriptor InputRule = new DiagnosticDescriptor(
        "SVM003", "Architecture policy is required", "{0}",
        "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd, WellKnownDiagnosticTags.NotConfigurable });

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(GraphRule, LayerRule, InputRule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationAction(Analyze);
    }

    private static void Analyze(CompilationAnalysisContext context)
    {
        var policy = context.Options.AdditionalFiles.SingleOrDefault(f => Path.GetFileName(f.Path) == "Architecture.xml");
        var policyText = policy?.GetText(context.CancellationToken);
        if (policy == null || policyText == null)
        {
            context.ReportDiagnostic(Diagnostic.Create(InputRule, Location.None, "Missing build/Architecture.xml compiler input."));
            return;
        }
        var root = Path.GetDirectoryName(Path.GetDirectoryName(policy.Path))!;
        var inputs = new List<KeyValuePair<string, string>>();
        foreach (var file in context.Options.AdditionalFiles.Where(f => f.Path.EndsWith(".csproj", StringComparison.Ordinal)))
        {
            var text = file.GetText(context.CancellationToken);
            if (text == null)
                context.ReportDiagnostic(Diagnostic.Create(InputRule, Location.None, "Cannot read " + file.Path));
            else inputs.Add(new KeyValuePair<string, string>(file.Path, text.ToString()));
        }
        foreach (var error in ArchitectureGraph.Validate(root, policyText.ToString(), inputs))
            context.ReportDiagnostic(Diagnostic.Create(GraphRule, Location.None, error));

        var assembly = context.Compilation.AssemblyName ?? "";
        if (!IsInnerLayer(assembly)) return;
        foreach (var reference in context.Compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (IsInfrastructure(reference.Name) || reference.Name.EndsWith("Tests", StringComparison.Ordinal))
                context.ReportDiagnostic(Diagnostic.Create(LayerRule, Location.None, assembly, reference.Name));
        }
    }

    private static bool IsInnerLayer(string name) => name == "Svm.SharedKernel" ||
        name.StartsWith("Svm.Core.", StringComparison.Ordinal) ||
        name.StartsWith("Svm.Services.", StringComparison.Ordinal) || name == "Svm.Application" ||
        new[] { "Svm.IdentityService", "Svm.ReleaseService", "Svm.PackageService", "Svm.InstanceService", "Svm.TaskService", "Svm.AuditService" }.Contains(name);

    private static bool IsInfrastructure(string name) =>
        name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
        name.StartsWith("Npgsql", StringComparison.Ordinal) || name.StartsWith("Dapper", StringComparison.Ordinal) ||
        name.StartsWith("MassTransit", StringComparison.Ordinal) || name.StartsWith("RabbitMQ", StringComparison.Ordinal) ||
        new[] { "Svm.EntityFrameworkCore", "Svm.Dapper", "Svm.EventBus", "Svm.Security" }.Contains(name);
}

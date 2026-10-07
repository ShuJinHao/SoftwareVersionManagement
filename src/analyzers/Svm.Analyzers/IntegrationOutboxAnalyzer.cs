using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Svm.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IntegrationOutboxAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor("SVM006", "Integration intents belong to atomic application handlers",
        "{0}", "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: new[] { WellKnownDiagnosticTags.NotConfigurable });
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.Field, SymbolKind.Property, SymbolKind.Method);
        context.RegisterOperationAction(AnalyzeOperation, OperationKind.Invocation, OperationKind.MethodReference, OperationKind.ObjectCreation);
    }
    private static bool Outbox(ITypeSymbol type) => type.ToDisplayString() == "Svm.Services.Contracts.Framework.IIntegrationEventOutbox" ||
        type is INamedTypeSymbol contract && contract.AllInterfaces.Any(i => i.ToDisplayString() == "Svm.Services.Contracts.Framework.IIntegrationEventOutbox") ||
        type is IArrayTypeSymbol array && Outbox(array.ElementType) || type is INamedTypeSymbol named && named.TypeArguments.Any(Outbox);
    private static bool Allowed(ISymbol symbol)
    {
        var assembly = symbol.ContainingAssembly.Name;
        if (assembly.EndsWith("Tests", StringComparison.Ordinal) || assembly == "Svm.Services.Contracts" || assembly == "Svm.EventBus" || assembly == "Svm.EntityFrameworkCore") return true;
        if (assembly != "Svm.Application") return false;
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        while (type != null)
        {
            if (type.AllInterfaces.Any(i => i.OriginalDefinition.MetadataName == "IIntegrationEventHandler`1" &&
                i.ContainingNamespace.ToDisplayString() == "Svm.Services.Contracts.Framework" && i.TypeArguments[0].ToDisplayString() is
                    "Svm.Services.Contracts.Messaging.V1.PackageWorkAvailableV1" or "Svm.Services.Contracts.Messaging.V1.TaskPreparationAvailableV1" or
                    "Svm.Services.Contracts.Messaging.V1.TaskControlAvailableV1")) return true;
            if (type.AllInterfaces.Any(i => i.OriginalDefinition.MetadataName == "IRequestHandler`2" &&
                i.ContainingNamespace.ToDisplayString() == "MediatR" && i.TypeArguments[0] is INamedTypeSymbol request &&
                request.AllInterfaces.Any(c => c.OriginalDefinition.MetadataName == "ICommand`1" && c.ContainingNamespace.ToDisplayString() == "Svm.Services.Contracts.Framework") && request.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == "Svm.Services.Contracts.Framework.RequestPolicyAttribute" &&
                    a.ConstructorArguments.Length > 4 && a.ConstructorArguments[4].Value is int transaction && transaction == 2))) return true;
            type = type.ContainingType;
        }
        return false;
    }
    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        if (Allowed(context.Symbol)) return;
        if (context.Symbol is IFieldSymbol field && Outbox(field.Type) || context.Symbol is IPropertySymbol property && Outbox(property.Type) ||
            context.Symbol is IMethodSymbol method && (Outbox(method.ReturnType) || method.Parameters.Any(p => Outbox(p.Type))))
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Symbol.Locations.FirstOrDefault(l => l.IsInSource),
                "Only an explicitly atomic Application command or registered integration Handler may depend on the integration outbox."));
    }
    private static void AnalyzeOperation(OperationAnalysisContext context)
    {
        if (Allowed(context.ContainingSymbol)) return;
        var type = context.Operation switch
        {
            IInvocationOperation call => (call.TargetMethod.ReducedFrom ?? call.TargetMethod).ContainingType,
            IMethodReferenceOperation reference => (reference.Method.ReducedFrom ?? reference.Method).ContainingType,
            IObjectCreationOperation creation => creation.Type, _ => null
        };
        if (type != null && Outbox(type)) context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(),
            "Queries, module code and domain event Handlers cannot stage integration messages."));
    }
}

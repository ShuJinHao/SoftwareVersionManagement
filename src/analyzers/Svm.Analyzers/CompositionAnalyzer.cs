using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Svm.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CompositionAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        "SVM004", "Composition and trusted identity belong to host adapters", "{0}",
        "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: new[] { WellKnownDiagnosticTags.NotConfigurable });

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation, OperationKind.MethodReference);
        context.RegisterOperationAction(AnalyzeCreation, OperationKind.ObjectCreation);
        context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.Field, SymbolKind.Property, SymbolKind.Method, SymbolKind.NamedType);
    }

    private static bool IsCompositionRoot(string? name) =>
        name == "Svm.HttpApi" || name == "Svm.Worker" || name == "Svm.Migration";

    private static bool IsFrameworkExecutor(ISymbol symbol) =>
        symbol.ContainingAssembly?.Name == "Svm.Services.CrossCutting" &&
        symbol.ContainingType?.ToDisplayString() == "Svm.Services.CrossCutting.Pipeline.ScopedRequestExecutor";

    private static bool IsBusiness(string? name) => name == "Svm.SharedKernel" || name == "Svm.Application" ||
        (name?.StartsWith("Svm.Core.", StringComparison.Ordinal) ?? false) ||
        name == "Svm.IdentityService" || name == "Svm.ReleaseService" || name == "Svm.PackageService" ||
        name == "Svm.InstanceService" || name == "Svm.TaskService" || name == "Svm.AuditService";

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        if (IsCompositionRoot(context.ContainingSymbol.ContainingAssembly?.Name) || IsFrameworkExecutor(context.ContainingSymbol)) return;
        var method = context.Operation is IInvocationOperation call ? call.TargetMethod : ((IMethodReferenceOperation)context.Operation).Method;
        method = method.ReducedFrom ?? method;
        var owner = method.ContainingType.ToDisplayString();
        var ns = method.ContainingNamespace.ToDisplayString();
        if (owner == "System.IServiceProvider" || owner == "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory" ||
            ns == "Microsoft.Extensions.DependencyInjection" &&
            (method.Name == "BuildServiceProvider" || method.Name == "GetService" || method.Name == "GetRequiredService" ||
             method.Name == "GetServices" || method.Name == "GetKeyedService" || method.Name == "GetRequiredKeyedService" ||
             method.Name == "GetKeyedServices" || method.Name == "CreateScope" || method.Name == "CreateAsyncScope"))
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), "Business/framework code cannot use a service locator or build a container."));
    }

    private static void AnalyzeCreation(OperationAnalysisContext context)
    {
        var operation = (IObjectCreationOperation)context.Operation;
        var type = operation.Type?.ToDisplayString();
        var assembly = context.ContainingSymbol.ContainingAssembly?.Name;
        if (!IsCompositionRoot(assembly) && (type == "MediatR.Mediator" || type == "Microsoft.Extensions.DependencyInjection.ServiceProvider"))
            context.ReportDiagnostic(Diagnostic.Create(Rule, operation.Syntax.GetLocation(), "Mediator/container construction belongs to the composition root."));
        if (IsBusiness(assembly) && (type == "Svm.Services.Contracts.Framework.CallActor" || type == "Svm.Services.Contracts.Framework.CallContextSnapshot"))
            context.ReportDiagnostic(Diagnostic.Create(Rule, operation.Syntax.GetLocation(), "Business code cannot manufacture a trusted actor or call context."));
    }

    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        if (IsCompositionRoot(context.Symbol.ContainingAssembly?.Name) || IsFrameworkExecutor(context.Symbol)) return;
        if (context.Symbol is INamedTypeSymbol named)
        {
            if (IsBusiness(named.ContainingAssembly?.Name))
                foreach (var contract in named.AllInterfaces)
                    if (contract.ToDisplayString() == "Svm.Services.Contracts.Framework.ITrustedCallContextSource")
                        Report(context, "A trusted context source must be supplied by a host adapter, not a business module.");
            return;
        }
        if (context.Symbol is IFieldSymbol field && IsContainerType(field.Type) ||
            context.Symbol is IPropertySymbol property && IsContainerType(property.Type))
            Report(context, "Do not store or expose a service provider/scope in business or shared code.");
        if (context.Symbol is IMethodSymbol method)
        {
            if (IsContainerType(method.ReturnType)) Report(context, "Do not return a service provider/scope.");
            foreach (var parameter in method.Parameters)
                if (IsContainerType(parameter.Type)) Report(context, "Use constructor-injected business ports instead of a service provider/scope.");
        }
    }

    private static bool IsContainerType(ITypeSymbol type)
    {
        var name = type.OriginalDefinition.ToDisplayString();
        if (name == "System.IServiceProvider" || name == "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory" ||
            name == "Microsoft.Extensions.DependencyInjection.IServiceScope" || name == "Microsoft.Extensions.DependencyInjection.AsyncServiceScope" ||
            name == "Microsoft.Extensions.DependencyInjection.ServiceProvider" ||
            name == "Svm.Services.CrossCutting.Pipeline.ScopedRequestExecutor") return true;
        if (type is IArrayTypeSymbol array) return IsContainerType(array.ElementType);
        if (type is INamedTypeSymbol named)
            foreach (var argument in named.TypeArguments)
                if (IsContainerType(argument)) return true;
        return false;
    }

    private static void Report(SymbolAnalysisContext context, string message)
    {
        foreach (var location in context.Symbol.Locations)
            if (location.IsInSource) { context.ReportDiagnostic(Diagnostic.Create(Rule, location, message)); break; }
    }
}

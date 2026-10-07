using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Svm.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DomainEventAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        "SVM005", "Domain event handlers stay inside their module and transaction", "{0}",
        "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: new[] { WellKnownDiagnosticTags.NotConfigurable });

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.NamedType, SymbolKind.Field, SymbolKind.Property, SymbolKind.Method);
        context.RegisterOperationAction(AnalyzeOperation, OperationKind.Invocation, OperationKind.MethodReference,
            OperationKind.ObjectCreation, OperationKind.FieldReference, OperationKind.PropertyReference);
    }

    private static bool IsHandler(INamedTypeSymbol? type) => type?.AllInterfaces.Any(IsContract) == true;
    private static bool IsContract(INamedTypeSymbol type) => type.OriginalDefinition.MetadataName == "IDomainEventHandler`1" &&
        type.ContainingNamespace.ToDisplayString() == "Svm.Services.Contracts.Framework";

    private static string? Owner(string? assembly) => assembly switch
    {
        "Svm.IdentityService" => "Identity", "Svm.ReleaseService" => "Releases", "Svm.PackageService" => "Packages",
        "Svm.InstanceService" => "Instances", "Svm.TaskService" => "Tasks", "Svm.AuditService" => "Audit", _ => null
    };

    private static INamedTypeSymbol? ContainingHandler(INamedTypeSymbol? type)
    {
        while (type != null)
        {
            if (IsHandler(type)) return type;
            type = type.ContainingType;
        }
        return null;
    }

    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        if (context.Symbol is INamedTypeSymbol named && IsHandler(named))
        {
            var owner = Owner(named.ContainingAssembly.Name);
            if (owner == null || named.AllInterfaces.Where(IsContract).Any(i =>
                    i.TypeArguments[0].ContainingAssembly?.Name != "Svm.Core." + owner))
                Report(context, "A domain event Handler and all its event types must belong to the same fixed module.");
            return;
        }
        var handler = ContainingHandler(context.Symbol.ContainingType);
        if (handler == null) return;
        var module = Owner(handler.ContainingAssembly.Name);
        if (context.Symbol is IFieldSymbol field && Forbidden(field.Type, module) ||
            context.Symbol is IPropertySymbol property && Forbidden(property.Type, module) ||
            context.Symbol is IMethodSymbol method && (Forbidden(method.ReturnType, module) || method.Parameters.Any(p => Forbidden(p.Type, module))))
            Report(context, "Domain event Handlers cannot depend on other modules, file/network/broker effects or transaction/dispatch orchestration.");
    }

    private static void AnalyzeOperation(OperationAnalysisContext context)
    {
        var handler = ContainingHandler(context.ContainingSymbol.ContainingType);
        if (handler == null) return;
        ITypeSymbol? target = context.Operation switch
        {
            IInvocationOperation call => (call.TargetMethod.ReducedFrom ?? call.TargetMethod).ContainingType,
            IMethodReferenceOperation reference => (reference.Method.ReducedFrom ?? reference.Method).ContainingType,
            IObjectCreationOperation creation => creation.Type,
            IFieldReferenceOperation field => field.Field.ContainingType,
            IPropertyReferenceOperation property => property.Property.ContainingType,
            _ => null
        };
        if (target != null && Forbidden(target, Owner(handler.ContainingAssembly.Name)))
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(),
                "Domain event Handlers only apply owning-module rules inside the current database transaction."));
    }

    private static bool Forbidden(ITypeSymbol type, string? owner)
    {
        var name = type.OriginalDefinition.ToDisplayString();
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        var assembly = type.ContainingAssembly?.Name ?? "";
        if (name == "System.IO.File" || name == "System.IO.Directory" || name == "System.IO.FileStream" ||
            name == "System.IO.FileSystemInfo" || name == "System.IO.FileInfo" || name == "System.IO.DirectoryInfo" || name == "System.IO.DriveInfo" ||
            name == "System.IO.StreamReader" || name == "System.IO.StreamWriter" || name == "System.IO.RandomAccess" ||
            name == "System.Net.WebRequest" || name == "System.Net.WebClient" || name == "System.Net.Dns" || name == "System.Diagnostics.Process" ||
            ns.StartsWith("System.Net.Http", StringComparison.Ordinal) || ns.StartsWith("System.Net.Sockets", StringComparison.Ordinal) ||
            ns.StartsWith("System.Net.Mail", StringComparison.Ordinal) || ns.StartsWith("MassTransit", StringComparison.Ordinal) ||
            ns.StartsWith("RabbitMQ.Client", StringComparison.Ordinal) || ns.StartsWith("Confluent.Kafka", StringComparison.Ordinal) ||
            name == "Svm.Services.Contracts.Framework.IUnitOfWork" || name == "Svm.Services.Contracts.Framework.IDomainEventDispatcher" ||
            name == "Svm.Services.Contracts.Framework.IOperationResultRecovery" || name == "Svm.Services.Contracts.Framework.IOperationResultStore" ||
            name == "Svm.Services.Contracts.Framework.IIntegrationEventOutbox" ||
            type is INamedTypeSymbol dispatcher && dispatcher.OriginalDefinition.MetadataName == "IIntegrationEventDispatcher`1" &&
                dispatcher.ContainingNamespace.ToDisplayString() == "Svm.Services.Contracts.Framework" ||
            name == "Svm.Services.Contracts.Framework.IIntegrationEventPreflight" ||
            name == "Svm.Services.Contracts.Framework.IIntegrationConsumptionRecovery" ||
            name == "Svm.Services.Contracts.Framework.IIntegrationWorkAuthorizer" ||
            name == "Svm.Services.Contracts.Framework.IIntegrationConsumptionTransaction") return true;
        if (assembly.StartsWith("Svm.Core.", StringComparison.Ordinal) && assembly != "Svm.Core." + owner ||
            Owner(assembly) is { } serviceOwner && serviceOwner != owner) return true;
        const string contracts = "Svm.Services.Contracts.";
        if (ns.StartsWith(contracts, StringComparison.Ordinal))
        {
            var part = ns.Substring(contracts.Length).Split('.')[0];
            if (part != "Framework" && part != owner) return true;
        }
        if (type is IArrayTypeSymbol array) return Forbidden(array.ElementType, owner);
        return type is INamedTypeSymbol named && named.TypeArguments.Any(t => Forbidden(t, owner));
    }

    private static void Report(SymbolAnalysisContext context, string message)
    {
        var location = context.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location != null) context.ReportDiagnostic(Diagnostic.Create(Rule, location, message));
    }
}

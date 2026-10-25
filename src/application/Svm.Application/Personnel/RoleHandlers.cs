using FluentValidation;
using MediatR;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.Application.Personnel;

internal sealed class ListRolesHandler(IRoleQueries roles) : IRequestHandler<ListRolesQuery, RolePage>
{ public Task<RolePage> Handle(ListRolesQuery r, CancellationToken t) => roles.ListAsync(r.Name, r.AfterId, r.PageSize, t); }
internal sealed class GetRoleHandler(IRoleQueries roles) : IRequestHandler<GetRoleQuery, RoleView>
{ public async Task<RoleView> Handle(GetRoleQuery r, CancellationToken t) => await roles.RoleAsync(r.RoleId, t) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
internal sealed class GetAuthorizationHandler(IRoleQueries roles) : IRequestHandler<GetAuthorizationQuery, AuthorizationView>
{ public Task<AuthorizationView> Handle(GetAuthorizationQuery r, CancellationToken t) => roles.GetAsync(r.SubjectId, t); }
internal sealed class GetMyAuthorizationHandler(IRoleQueries roles, ISessionProofSource proof) : IRequestHandler<GetMyAuthorizationQuery, AuthorizationView>
{ public Task<AuthorizationView> Handle(GetMyAuthorizationQuery r, CancellationToken t) => roles.GetAsync(proof.Proof?.SubjectId ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired), t); }

internal sealed class RoleCompletion(IPersonnelService personnel, ISessionProofSource proof, IAuditWriter audit, ICallContext calls, IUnitOfWork unit)
{
    internal async Task<OperationResult<T>> Execute<T>(string operation, string reason, Func<Task<T>> action, Func<T, Guid> id, CancellationToken t)
    {
        var actor = await personnel.AuthenticateAsync(proof.Proof ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired), true, t)
            ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var result = await action(); var op = unit.CurrentOperationId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        audit.Append(new(op, actor.SubjectId, "Human", actor.EmployeeNo, actor.DisplayName, operation, id(result), "succeeded", reason,
            calls.Current?.CorrelationId ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired)));
        return OperationResult<T>.Completed(op, result, id(result));
    }
}
internal sealed class CreateRoleHandler(IRoleAuthorization roles, RoleCompletion completion) : IRequestHandler<CreateRoleCommand, OperationResult<RoleView>>
{ public Task<OperationResult<RoleView>> Handle(CreateRoleCommand r, CancellationToken t) => completion.Execute("identity.roles.create", r.Reason, () => roles.CreateAsync(r.Name, r.Operations, t), x => x.Id, t); }
internal sealed class UpdateRoleHandler(IRoleAuthorization roles, RoleCompletion completion) : IRequestHandler<UpdateRoleCommand, OperationResult<RoleView>>
{ public Task<OperationResult<RoleView>> Handle(UpdateRoleCommand r, CancellationToken t) => completion.Execute("identity.roles.update", r.Reason, () => roles.UpdateAsync(r.RoleId, r.ExpectedRevision, r.Name, r.IsEnabled, r.Operations, t), x => x.Id, t); }
internal sealed class ReplaceAuthorizationHandler(IRoleAuthorization roles, ISiteAssets assets, ISoftwareCatalog software, RoleCompletion completion)
    : IRequestHandler<ReplaceAuthorizationCommand, OperationResult<AuthorizationView>>
{
    public Task<OperationResult<AuthorizationView>> Handle(ReplaceAuthorizationCommand r, CancellationToken t) => completion.Execute("identity.authorization.replace", r.Reason, async () =>
    {
        await assets.EnsureDeploymentAsync(true, t);
        foreach (var id in r.Assignments.SelectMany(a => a.Scopes).Where(s => s.Kind == "Software").Select(s => s.ResourceId!.Value)
            .Concat(r.DirectPermissions.Where(p => p.SoftwareId is not null).Select(p => p.SoftwareId!.Value)).Distinct().Order())
            if (!await software.ExistsAsync(id, true, t)) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        foreach (var s in r.Assignments.SelectMany(a => a.Scopes).Distinct().OrderBy(s => s.Kind).ThenBy(s => s.ResourceId))
        {
            if (s.Kind == "Process") await assets.ProcessAsync(s.ResourceId!.Value, true, t);
            if (s.Kind == "Device") await assets.DeviceAsync(s.ResourceId!.Value, true, t);
        }
        return await roles.ReplaceAsync(r.SubjectId, r.ExpectedRevision, r.Assignments, r.DirectPermissions, t);
    }, x => x.SubjectId, t);
}

internal static class RoleValidation
{
    internal static bool Operations(IReadOnlyList<string> ops) => ops is not null && ops.Count is > 0 and <= 32 && ops.Distinct().Count() == ops.Count && ops.All(RoleOperationCatalog.IsOpen);
    internal static bool Assignments(IReadOnlyList<RoleAssignmentInput> a) => a is not null && a.Count <= 32 && a.Select(x => x.RoleId).Distinct().Count() == a.Count &&
        a.All(x => x.RoleId != Guid.Empty && x.Scopes is { Count: > 0 and <= 100 } &&
            x.Scopes.All(s => s is not null && s.Name is null && (s.Kind == "Site" && s.ResourceId is null ||
                s.Kind is "Process" or "Device" or "Software" && s.ResourceId is { } id && id != Guid.Empty)) && x.Scopes.Distinct().Count() == x.Scopes.Count);
}
internal sealed class ListRolesValidator : AbstractValidator<ListRolesQuery>
{ public ListRolesValidator() { RuleFor(x => x.PageSize).InclusiveBetween(1, 200); RuleFor(x => x.Name).MaximumLength(128); RuleFor(x => x.AfterId).Must(x => x is null || x != Guid.Empty); } }
internal sealed class GetRoleValidator : AbstractValidator<GetRoleQuery>
{ public GetRoleValidator() => RuleFor(x => x.RoleId).NotEmpty(); }
internal sealed class GetAuthorizationValidator : AbstractValidator<GetAuthorizationQuery>
{ public GetAuthorizationValidator() => RuleFor(x => x.SubjectId).NotEmpty(); }
internal sealed class GetMyAuthorizationValidator : AbstractValidator<GetMyAuthorizationQuery> { }
internal sealed class CreateRoleValidator : AbstractValidator<CreateRoleCommand>
{ public CreateRoleValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.Name).NotEmpty().MaximumLength(128); RuleFor(x => x.Operations).Must(RoleValidation.Operations); RuleFor(x => x.Reason).NotEmpty().MaximumLength(256); } }
internal sealed class UpdateRoleValidator : AbstractValidator<UpdateRoleCommand>
{ public UpdateRoleValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.RoleId).NotEmpty(); RuleFor(x => x.ExpectedRevision).GreaterThan(0); RuleFor(x => x.Name).NotEmpty().MaximumLength(128); RuleFor(x => x.Operations).Must(RoleValidation.Operations); RuleFor(x => x.Reason).NotEmpty().MaximumLength(256); } }
internal sealed class ReplaceAuthorizationValidator : AbstractValidator<ReplaceAuthorizationCommand>
{ public ReplaceAuthorizationValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.SubjectId).NotEmpty(); RuleFor(x => x.ExpectedRevision).GreaterThan(0); RuleFor(x => x.Assignments).Must(RoleValidation.Assignments);
    RuleFor(x => x.DirectPermissions).Must(p => p is not null && p.Count <= 100 && p.All(v => v is not null && v.SoftwareId != Guid.Empty && !string.IsNullOrWhiteSpace(v.Operation) && v.Operation.Length <= 64) && p.Distinct().Count() == p.Count);
    RuleFor(x => x.Reason).NotEmpty().MaximumLength(256); } }

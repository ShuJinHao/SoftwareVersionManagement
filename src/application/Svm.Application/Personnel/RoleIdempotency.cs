using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.Application.Personnel;

internal abstract class RoleAdapter<T>(IRoleQueries roles) : IIdempotencyRequestAdapter<T, OperationResult<RoleView>> where T : notnull
{
    public abstract OperationRequestData Describe(T request);
    public OperationResultReference GetReference(OperationResult<RoleView> response) => new(response.OperationId, response.Status, response.ResourceId);
    public async Task<OperationResult<RoleView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var role = await roles.RoleAsync(reference.ResourceId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid), token)
            ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        return OperationResult<RoleView>.Completed(reference.OperationId, role, role.Id);
    }
    protected static OperationValue Ops(IReadOnlyList<string> values) => OperationValue.Array(values.Select(OperationValue.Text).ToArray());
}
internal sealed class CreateRoleAdapter(IRoleQueries roles) : RoleAdapter<CreateRoleCommand>(roles)
{
    public override OperationRequestData Describe(CreateRoleCommand r) => new(r.Key, OperationValue.Object(), OperationValue.Object(
        new("name", OperationValue.Text(r.Name)), new("operations", Ops(r.Operations)), new("reason", OperationValue.Text(r.Reason))));
}
internal sealed class UpdateRoleAdapter(IRoleQueries roles) : RoleAdapter<UpdateRoleCommand>(roles)
{
    public override OperationRequestData Describe(UpdateRoleCommand r) => new(r.Key, OperationValue.Object(new OperationField("roleId", OperationValue.Identifier(r.RoleId))), OperationValue.Object(
        new("name", OperationValue.Text(r.Name)), new("operations", Ops(r.Operations)), new("isEnabled", OperationValue.Boolean(r.IsEnabled)),
        new("expectedRevision", OperationValue.Integer(r.ExpectedRevision)), new("reason", OperationValue.Text(r.Reason))));
}
internal sealed class ReplaceAuthorizationAdapter(IRoleQueries roles) : IIdempotencyRequestAdapter<ReplaceAuthorizationCommand, OperationResult<AuthorizationView>>
{
    public OperationRequestData Describe(ReplaceAuthorizationCommand r) => new(r.Key, OperationValue.Object(new OperationField("subjectId", OperationValue.Identifier(r.SubjectId))), OperationValue.Object(
        new("expectedRevision", OperationValue.Integer(r.ExpectedRevision)), new("reason", OperationValue.Text(r.Reason)),
        new("assignments", OperationValue.Array(r.Assignments.Select(a => OperationValue.Object(new OperationField("roleId", OperationValue.Identifier(a.RoleId)),
            new("scopes", OperationValue.Array(a.Scopes.Select(s => OperationValue.Object(new("kind", OperationValue.Text(s.Kind)),
                new("resourceId", s.ResourceId is { } id ? OperationValue.Identifier(id) : OperationValue.Null))).ToArray())))).ToArray())),
        new("directPermissions", OperationValue.Array(r.DirectPermissions.Select(p => OperationValue.Object(new("operation", OperationValue.Text(p.Operation)),
            new("softwareId", p.SoftwareId is { } id ? OperationValue.Identifier(id) : OperationValue.Null))).ToArray()))));
    public OperationResultReference GetReference(OperationResult<AuthorizationView> response) => new(response.OperationId, response.Status, response.ResourceId);
    public async Task<OperationResult<AuthorizationView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var value = await roles.GetAsync(reference.ResourceId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid), token);
        return OperationResult<AuthorizationView>.Completed(reference.OperationId, value, value.SubjectId);
    }
}

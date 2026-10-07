using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.Application.Personnel;

internal abstract class UserAdapter<T>(IUserQueries users) : IIdempotencyRequestAdapter<T, OperationResult<UserView>> where T : notnull
{
    public abstract OperationRequestData Describe(T request);
    public OperationResultReference GetReference(OperationResult<UserView> response) =>
        new(response.OperationId, response.Status, response.ResourceId);
    public async Task<OperationResult<UserView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } userId)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var user = await users.GetAsync(userId, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        return OperationResult<UserView>.Completed(reference.OperationId, user, user.Id);
    }
    protected static OperationRequestData Project(Guid key, Guid? target, params OperationField[] body) =>
        new(key, target is null ? OperationValue.Object() : OperationValue.Object(new OperationField("userId", OperationValue.Identifier(target.Value))), OperationValue.Object(body));
    protected static OperationValue Text(string? value) => value is null ? OperationValue.Null : OperationValue.Text(value);
}
internal sealed class CreateUserAdapter(IUserQueries users) : UserAdapter<CreateUserCommand>(users)
{
    public override OperationRequestData Describe(CreateUserCommand x) => Project(x.Key, null,
        new("employeeNo", Text(x.EmployeeNo)), new("displayName", Text(x.DisplayName)), new("temporaryPassword", Text(x.TemporaryPassword)));
}
internal sealed class UpdateUserAdapter(IUserQueries users) : UserAdapter<UpdateUserCommand>(users)
{
    public override OperationRequestData Describe(UpdateUserCommand x) => Project(x.Key, x.UserId,
        new("expectedRevision", OperationValue.Integer(x.ExpectedRevision)), new("displayName", Text(x.DisplayName)),
        new("isEnabled", x.IsEnabled is { } enabled ? OperationValue.Boolean(enabled) : OperationValue.Null), new("reason", Text(x.Reason)));
}
internal sealed class ResetUserPasswordAdapter(IUserQueries users) : UserAdapter<ResetUserPasswordCommand>(users)
{
    public override OperationRequestData Describe(ResetUserPasswordCommand x) => Project(x.Key, x.UserId,
        new("expectedRevision", OperationValue.Integer(x.ExpectedRevision)), new("temporaryPassword", Text(x.TemporaryPassword)), new("reason", Text(x.Reason)));
}
internal sealed class ReplaceUserPermissionsAdapter(IUserQueries users) : UserAdapter<ReplaceUserPermissionsCommand>(users)
{
    public override OperationRequestData Describe(ReplaceUserPermissionsCommand x) => Project(x.Key, x.UserId,
        new("expectedRevision", OperationValue.Integer(x.ExpectedRevision)), new("reason", Text(x.Reason)),
        new("permissions", OperationValue.Array(x.Permissions.Select(p => OperationValue.Object(
            new("softwareId", p.SoftwareId is { } id ? OperationValue.Identifier(id) : OperationValue.Null), new("operation", Text(p.Operation)))).ToArray())));
}

using FluentValidation;
using MediatR;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.Application.Personnel;

internal sealed class GetUserHandler(IUserQueries users) : IRequestHandler<GetUserQuery, UserView>
{
    public async Task<UserView> Handle(GetUserQuery request, CancellationToken token) =>
        await users.GetAsync(request.UserId, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
}
internal sealed class ListUsersHandler(IUserQueries users) : IRequestHandler<ListUsersQuery, UserPage>
{
    public Task<UserPage> Handle(ListUsersQuery request, CancellationToken token) => users.ListAsync(request.Input, token);
}
internal sealed class PersonnelAdministrationCompletion(IPersonnelService personnel, ISessionProofSource proof,
    IAuditWriter audit, ICallContext calls, IUnitOfWork unitOfWork)
{
    internal async Task<OperationResult<UserView>> ExecuteAsync(string operation, string reason, Func<Task<UserView>> action, CancellationToken token)
    {
        var actor = await personnel.AuthenticateAsync(proof.Proof ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired), true, token)
            ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var view = await action();
        var id = unitOfWork.CurrentOperationId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var call = calls.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        audit.Append(new(id, actor.SubjectId, "Human", actor.EmployeeNo, actor.DisplayName, operation, view.Id, "succeeded", reason, call.CorrelationId));
        return OperationResult<UserView>.Completed(id, view, view.Id);
    }
}
internal sealed class CreateUserHandler(IPersonnelAdministration personnel, PersonnelAdministrationCompletion completion)
    : IRequestHandler<CreateUserCommand, OperationResult<UserView>>
{
    public Task<OperationResult<UserView>> Handle(CreateUserCommand request, CancellationToken token) =>
        completion.ExecuteAsync("identity.users.create", "create personnel account", () =>
            personnel.CreateAsync(request.EmployeeNo, request.DisplayName, request.TemporaryPassword, token), token);
}
internal sealed class UpdateUserHandler(IPersonnelAdministration personnel, PersonnelAdministrationCompletion completion)
    : IRequestHandler<UpdateUserCommand, OperationResult<UserView>>
{
    public Task<OperationResult<UserView>> Handle(UpdateUserCommand request, CancellationToken token) =>
        completion.ExecuteAsync("identity.users.update", request.Reason, () =>
            personnel.UpdateAsync(request.UserId, request.ExpectedRevision, request.DisplayName, request.IsEnabled, token), token);
}
internal sealed class ResetUserPasswordHandler(IPersonnelAdministration personnel, PersonnelAdministrationCompletion completion)
    : IRequestHandler<ResetUserPasswordCommand, OperationResult<UserView>>
{
    public Task<OperationResult<UserView>> Handle(ResetUserPasswordCommand request, CancellationToken token) =>
        completion.ExecuteAsync("identity.users.reset-password", request.Reason, () =>
            personnel.ResetPasswordAsync(request.UserId, request.ExpectedRevision, request.TemporaryPassword, token), token);
}
internal sealed class ReplaceUserPermissionsHandler(IPersonnelAdministration personnel, PersonnelAdministrationCompletion completion)
    : IRequestHandler<ReplaceUserPermissionsCommand, OperationResult<UserView>>
{
    public Task<OperationResult<UserView>> Handle(ReplaceUserPermissionsCommand request, CancellationToken token) =>
        completion.ExecuteAsync("identity.users.permissions", request.Reason, () =>
            personnel.ReplacePermissionsAsync(request.UserId, request.ExpectedRevision, request.Permissions, token), token);
}

internal sealed class GetUserValidator : AbstractValidator<GetUserQuery>
{
    public GetUserValidator() => RuleFor(x => x.UserId).NotEmpty();
}
internal sealed class ListUsersValidator : AbstractValidator<ListUsersQuery>
{
    public ListUsersValidator(PersonnelManagementOptions options)
    {
        RuleFor(x => x.Input).NotNull();
        RuleFor(x => x.Input.PageSize).InclusiveBetween(1, options.MaximumPageSize);
        RuleFor(x => x.Input.EmployeeNo).MaximumLength(64).Matches("^[A-Za-z0-9._-]*$").When(x => x.Input.EmployeeNo is not null);
        RuleFor(x => x.Input.After).Must(p => p is null || p.Id != Guid.Empty && !string.IsNullOrWhiteSpace(p.EmployeeNo) && p.EmployeeNo.Length <= 64);
    }
}
internal sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator(PersonnelPolicy policy)
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.EmployeeNo).NotEmpty().MaximumLength(64).Matches("^[A-Za-z0-9._-]+$");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(128);
        RuleFor(x => x.TemporaryPassword).NotEmpty().Length(policy.MinimumPasswordLength, policy.MaximumPasswordLength);
    }
}
internal sealed class UpdateUserValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.UserId).NotEmpty(); RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(128).When(x => x.DisplayName is not null);
        RuleFor(x => x).Must(x => x.DisplayName is not null || x.IsEnabled is not null);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class ResetUserPasswordValidator : AbstractValidator<ResetUserPasswordCommand>
{
    public ResetUserPasswordValidator(PersonnelPolicy policy)
    {
        RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.UserId).NotEmpty(); RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.TemporaryPassword).NotEmpty().Length(policy.MinimumPasswordLength, policy.MaximumPasswordLength);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class ReplaceUserPermissionsValidator : AbstractValidator<ReplaceUserPermissionsCommand>
{
    public ReplaceUserPermissionsValidator()
    {
        RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.UserId).NotEmpty(); RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Permissions).Must(p => p is not null && p.Count <= 100 && p.All(v => v is not null &&
            v.SoftwareId != Guid.Empty && !string.IsNullOrWhiteSpace(v.Operation) && v.Operation.Length <= 64) && p.Distinct().Count() == p.Count);
    }
}

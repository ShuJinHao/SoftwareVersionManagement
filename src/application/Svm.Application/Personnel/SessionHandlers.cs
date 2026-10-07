using FluentValidation;
using MediatR;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.Application.Personnel;

internal sealed class AnonymousSessionHandler : IRequestHandler<AnonymousSessionQuery, PersonnelView?>
{
    public Task<PersonnelView?> Handle(AnonymousSessionQuery request, CancellationToken cancellationToken) => Task.FromResult<PersonnelView?>(null);
}
internal sealed class CurrentSessionHandler(IPersonnelService personnel, ISessionProofSource source) : IRequestHandler<CurrentSessionQuery, PersonnelView?>
{
    public Task<PersonnelView?> Handle(CurrentSessionQuery request, CancellationToken cancellationToken) =>
        personnel.AuthenticateAsync(source.Proof ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired), false, cancellationToken);
}
internal sealed class PersonnelCompletion(IAuditWriter audit, IUnitOfWork unitOfWork, ICallContext context)
{
    public OperationResult<PersonnelMutation> Complete(string operation, PersonnelMutation mutation)
    {
        var id = unitOfWork.CurrentOperationId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var call = context.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        if (mutation.Changed)
        {
            var person = mutation.Person;
            audit.Append(new AuditFact(id, call.Actor.Kind == ActorKind.Anonymous ? person?.SubjectId : call.Actor.ActorId,
                call.Actor.Kind == ActorKind.Anonymous && person is not null ? "Human" : call.Actor.Kind.ToString(),
                call.Actor.Kind == ActorKind.Service ? null : person?.EmployeeNo,
                call.Actor.Kind == ActorKind.Service ? null : person?.DisplayName, operation, person?.SubjectId,
                mutation.Failure is null ? "succeeded" : "rejected", operation == "identity.seed" ? "explicit initialization" : "self service",
                call.CorrelationId));
        }
        return OperationResult<PersonnelMutation>.Completed(id, mutation);
    }
}
internal sealed class LoginHandler(IPersonnelService personnel, ISessionProofSource source, PersonnelCompletion completion)
    : IRequestHandler<LoginCommand, OperationResult<PersonnelMutation>>
{
    public async Task<OperationResult<PersonnelMutation>> Handle(LoginCommand request, CancellationToken cancellationToken) =>
        completion.Complete("session.login", await personnel.LoginAsync(request.EmployeeNo, request.Password, source.SourceAddress, cancellationToken));
}
internal sealed class LogoutHandler(IPersonnelService personnel, ISessionProofSource source, PersonnelCompletion completion)
    : IRequestHandler<LogoutCommand, OperationResult<PersonnelMutation>>
{
    public async Task<OperationResult<PersonnelMutation>> Handle(LogoutCommand request, CancellationToken cancellationToken) =>
        completion.Complete("session.logout", await personnel.LogoutAsync(source.Proof ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired), cancellationToken));
}
internal sealed class PasswordHandler(IPersonnelService personnel, ISessionProofSource source, PersonnelCompletion completion)
    : IRequestHandler<ChangePasswordCommand, OperationResult<PersonnelMutation>>
{
    public async Task<OperationResult<PersonnelMutation>> Handle(ChangePasswordCommand request, CancellationToken cancellationToken) =>
        completion.Complete("session.password", await personnel.ChangePasswordAsync(source.Proof ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired),
            request.CurrentPassword, request.NewPassword, source.SourceAddress, cancellationToken));
}
internal sealed class SeedHandler(IPersonnelService personnel, PersonnelCompletion completion) : IRequestHandler<SeedPersonnelCommand, OperationResult<PersonnelMutation>>
{
    public async Task<OperationResult<PersonnelMutation>> Handle(SeedPersonnelCommand request, CancellationToken cancellationToken) =>
        completion.Complete("identity.seed", await personnel.SeedAsync(request.EmployeeNo, request.DisplayName, request.TemporaryPassword, cancellationToken));
}
internal sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.EmployeeNo).NotEmpty().MaximumLength(64).Matches("^[A-Za-z0-9._-]+$");
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}
internal sealed class PasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public PasswordValidator(PersonnelPolicy policy)
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(128);
        RuleFor(x => x.NewPassword).NotEmpty().Length(policy.MinimumPasswordLength, policy.MaximumPasswordLength);
    }
}
internal sealed class SeedValidator : AbstractValidator<SeedPersonnelCommand>
{
    public SeedValidator(PersonnelPolicy policy)
    {
        RuleFor(x => x.EmployeeNo).NotEmpty().MaximumLength(64).Matches("^[A-Za-z0-9._-]+$");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(128);
        RuleFor(x => x.TemporaryPassword).NotEmpty().Length(policy.MinimumPasswordLength, policy.MaximumPasswordLength);
    }
}

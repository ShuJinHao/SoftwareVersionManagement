using FluentValidation;
using MediatR;
using Svm.Services.Contracts.Framework;

namespace Svm.Services.CrossCutting.Pipeline;

public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var errors = new List<ValidationIssue>();
        foreach (var validator in validators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Scoped dependencies are not run concurrently against the same request scope.
            var result = await validator.ValidateAsync(new ValidationContext<TRequest>(request), cancellationToken);
            errors.AddRange(result.Errors.Select(f => new ValidationIssue(f.PropertyName, f.ErrorCode, f.ErrorMessage)));
        }
        if (errors.Count != 0) throw new RequestRejectedException(RequestFailure.ValidationFailed, errors);
        cancellationToken.ThrowIfCancellationRequested();
        return await next();
    }
}

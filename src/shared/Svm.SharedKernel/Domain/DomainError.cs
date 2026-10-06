namespace Svm.SharedKernel.Domain;

public sealed record DomainError
{
    public DomainError(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
    }

    public string Code { get; }
    public string Message { get; }
}

public sealed class DomainException : Exception
{
    public DomainException(DomainError error) : base((error ?? throw new ArgumentNullException(nameof(error))).Message) => Error = error;
    public DomainError Error { get; }
}

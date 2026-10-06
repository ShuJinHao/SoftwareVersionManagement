using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Svm.Services.Contracts.Identity;

namespace Svm.Security;

public static class PersonnelCryptoRegistration
{
    public static IServiceCollection AddSvmPersonnelCrypto(this IServiceCollection services, PersonnelPolicy policy)
    {
        policy.Validate();
        services.AddSingleton(policy);
        services.AddSingleton<IPersonnelCrypto, PersonnelCrypto>();
        return services;
    }
}
internal sealed class PersonnelCrypto : IPersonnelCrypto
{
    private readonly PasswordHasher<object> _hasher;
    private readonly object _subject = new();
    public PersonnelCrypto(PersonnelPolicy policy)
    {
        _hasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions
            { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3, IterationCount = policy.HashIterations }));
        DummyPasswordHash = HashPassword(CreateSecret());
    }
    public string DummyPasswordHash { get; }
    public string HashPassword(string password) => _hasher.HashPassword(_subject, password);
    public bool VerifyPassword(string hash, string password)
    {
        try { return _hasher.VerifyHashedPassword(_subject, hash, password) != PasswordVerificationResult.Failed; }
        catch (FormatException) { return false; }
    }
    public string CreateSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string HashSecret(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public bool VerifySecret(string hash, string secret) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(HashSecret(secret)));
}

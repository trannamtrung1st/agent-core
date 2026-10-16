using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AgentCore.Infrastructure.Security;

public sealed class AccountPasswordHasher : IAccountPasswordHasher
{
    private readonly PasswordHasher<Account> _hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
    private readonly string _dummyHash;
    public AccountPasswordHasher() => _dummyHash = _hasher.HashPassword(null!, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    public string Hash(Account account, string password) => _hasher.HashPassword(account, password);
    public bool Verify(Account account, string password) => password.Length <= 256
        && _hasher.VerifyHashedPassword(account, string.IsNullOrEmpty(account.PasswordHash) ? _dummyHash : account.PasswordHash, password) != PasswordVerificationResult.Failed;
}

public sealed class IdentityTokens : IIdentityTokens
{
    public string Create() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    public bool Matches(string token, string hash) => hash.Length == 64
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(hash));
}

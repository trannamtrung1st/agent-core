using Microsoft.Extensions.Configuration;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Email;

namespace AgentCore.Infrastructure.Tests;

public sealed class GoogleOAuthProofTests
{
    [Fact]
    public async Task Production_ID_verifier_rejects_malformed_JWT_without_identity_claims()
    { await Assert.ThrowsAnyAsync<Exception>(() => GoogleOAuthProof.VerifyIdentity("forged-token", "client", "nonce", default)); }
    [Theory]
    [InlineData("http://public.example.test/")]
    [InlineData("https://trusted.example.test/other")]
    [InlineData("https://user@trusted.example.test/")]
    [InlineData("https://trusted.example.test/?returnUrl=https://evil.example")]
    [InlineData("https://trusted.example.test/#fragment")]
    public void OAuth_public_origin_rejects_unsafe_or_ambiguous_configuration(string value)
    { Assert.Throws<InvalidOperationException>(() => new GoogleOAuthConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleOAuth:ClientId"] = "client", ["GoogleOAuth:ClientSecret"] = "secret", ["GoogleOAuth:PublicAppUrl"] = value }).Build())); }
}

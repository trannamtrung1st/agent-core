using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuth_expiry_worker_clears_expired_private_proof_without_grant_or_authority_effects(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T07:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var view = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, OAuthInput(CredentialPurpose.GoogleDriveAccount), 1);
        var id = view.Credential.CredentialId; var current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        var a = OAuthAttempt(f, owner.TenantId, current, new(owner.AccountId, session.SessionId, owner.OwnerPrincipalId)); Assert.True(await BeginOAuth(f, a));
        var before = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var audits = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        clock.Advance(TimeSpan.FromMinutes(5));
        var observer = new ExpiryLog();
        using var worker = new CredentialOAuthExpiryWorker((ITenantCredentialOAuthStore)f.Iam, clock, observer);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await observer.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var expired = (await ((ITenantCredentialOAuthStore)f.Iam).ReadCredentialOAuthAttemptAsync(a.AttemptId))!;
            Assert.Equal(CredentialOAuthOutcome.Expired, expired.Outcome); Assert.Empty(expired.ProtectedProof);
            var retained = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
            Assert.Empty(retained.ProtectedPayload); Assert.Equal(current.Revision, retained.Revision); Assert.Equal(current.SecretRevision, retained.SecretRevision);
            Assert.Equal(before.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
            Assert.Equal(audits, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }
    private sealed class ExpiryLog : ILogger<CredentialOAuthExpiryWorker>
    {
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (id.Id == 4301) Published.TrySetResult(); }
    }
}

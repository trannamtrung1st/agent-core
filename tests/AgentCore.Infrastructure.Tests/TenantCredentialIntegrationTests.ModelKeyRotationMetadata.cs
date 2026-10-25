using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_key_rotation_metadata_uses_owned_audited_replacements_not_later_metadata_edits(bool sqlite)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await ProviderSetup(f, ModelProviderKind.OpenAI);
        var tenant = setup.Route.TenantId; var human = setup.Human; var id = setup.Connection.CredentialId!.Value;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        Assert.Null((await f.Service.ReadAsync(human.PrincipalId, tenant, id, initiator: human)).LastRotatedAt);
        clock.Advance(TimeSpan.FromHours(1)); var replacedAt = clock.GetUtcNow();
        var replaced = await f.Service.ReplaceAsync(human.PrincipalId, tenant, id, "replacement-private-key", await Revision(), 1, initiator: human);
        Assert.Equal(replacedAt, replaced.LastRotatedAt);
        clock.Advance(TimeSpan.FromHours(1));
        var renamed = await f.Service.UpdateAsync(human.PrincipalId, tenant, id, new("Renamed inference key", CredentialStatus.Active, null, []), await Revision(), 2, initiator: human);
        Assert.Equal(replacedAt, renamed.LastRotatedAt);
        var read = await f.Service.ReadAsync(human.PrincipalId, tenant, id, initiator: human);
        Assert.Equal(replacedAt, read.LastRotatedAt); Assert.True(read.Credential.UpdatedAtUtc > read.LastRotatedAt);
        Assert.Equal(replacedAt, Assert.Single((await f.Service.ListAsync(human.PrincipalId, tenant, initiator: human)).Items).LastRotatedAt);
        Assert.DoesNotContain("replacement-private-key", JsonSerializer.Serialize(read));
        Assert.DoesNotContain("ProtectedPayload", JsonSerializer.Serialize(read));
        Assert.Empty(await f.Credentials.ReadTenantCredentialRotationsAsync(Guid.NewGuid(), [id]));
        Assert.Empty(await f.Credentials.ReadTenantCredentialRotationsAsync(tenant, [Guid.NewGuid()]));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Credentials.ReadTenantCredentialRotationsAsync(tenant, Enumerable.Repeat(id, 101).ToArray()).AsTask());
        if (sqlite) Assert.Equal(replacedAt, (await new SqliteIamStore(f.Contexts!).ReadTenantCredentialRotationsAsync(tenant, [id]))[id]);
        await f.Iam.RevokeSessionAsync(human.AuthenticationSessionId, clock.GetUtcNow());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Service.ReadAsync(human.PrincipalId, tenant, id, initiator: human).AsTask());
    }
}

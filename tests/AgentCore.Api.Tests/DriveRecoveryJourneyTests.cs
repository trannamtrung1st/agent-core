using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class DriveJourneyTests
{
    private static async Task<(DriveService Service, DriveAccess Access, LostFactory Fault)> Recovery(Factory host, string mode)
    {
        var client = host.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var credentials = host.Services.GetRequiredService<CredentialService>();
        var account = await credentials.CreateGoogleDriveAccountAsync("Recovery");
        var oauth = host.Services.GetRequiredService<IGoogleDriveOAuthService>();
        var profile = (await host.Services.GetRequiredService<ILocalUserProfileService>().GetLocalProfileAsync()).ProfileId;
        var start = await oauth.StartAsync(account.CredentialId, 1, profile, default);
        var state = QueryHelpers.ParseQuery(start.AuthorizationUrl[start.AuthorizationUrl.IndexOf('?')..])["state"].ToString();
        await oauth.CompleteAsync(state, start.Correlation, profile, "consent", null, default);
        var made = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 27 });
        made.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await made.Content.ReadAsStringAsync());
        var owner = json.RootElement.GetProperty("instanceId").GetGuid();
        await credentials.BindAsync(owner, account.CredentialId, "drive", 1);
        var fault = new LostFactory(host.Services.GetRequiredService<IGoogleDriveProviderFactory>()) { Mode = mode,
            Operations = host.Services.GetRequiredService<IDriveOperationStore>() };
        var service = RecoveryService(host, fault);
        return (service, await service.ResolveAsync(owner, "drive", null, true, default), fault);
    }
    private static DriveService RecoveryService(Factory host, IGoogleDriveProviderFactory provider) => new(
        host.Services.GetRequiredService<ICredentialStore>(), host.Services.GetRequiredService<IAgentCredentialBindingStore>(),
        host.Services.GetRequiredService<IAgentInstanceStore>(), provider, host.Services.GetRequiredService<IDriveOperationStore>(), TimeProvider.System);
    private static readonly byte[] RecoveryBytes = "hi"u8.ToArray();
    private static DriveEffect RecoveryEffect => new("report.txt", "root", Convert.ToHexStringLower(SHA256.HashData(RecoveryBytes)), 2, "text/plain");

    [Fact]
    public async Task Reconnect_does_not_authorize_content_continuation_under_a_new_epoch()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "partial"); var id = Guid.NewGuid();
        await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        var oauth = host.Services.GetRequiredService<IGoogleDriveOAuthService>();
        await oauth.DisconnectAsync(access.Credential.CredentialId, access.Credential.Revision, default);
        var current = await host.Services.GetRequiredService<CredentialService>().GetAsync(access.Credential.CredentialId);
        var profile = (await host.Services.GetRequiredService<ILocalUserProfileService>().GetLocalProfileAsync()).ProfileId;
        var start = await oauth.StartAsync(current.CredentialId, current.Revision, profile, default);
        var state = QueryHelpers.ParseQuery(start.AuthorizationUrl[start.AuthorizationUrl.IndexOf('?')..])["state"].ToString();
        await oauth.CompleteAsync(state, start.Correlation, profile, "consent", null, default);
        fault.Mode = "none"; access = await service.ResolveAsync(access.InstanceId, "drive", null, true, default);
        var blocked = await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, id, "drive.upload", 3, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        Assert.False(blocked.ResumeAllowed); Assert.Equal(1, fault.Uploads);
        Assert.DoesNotContain("protected-test-session", blocked.Message);
        Assert.Equal(1, (await host.Services.GetRequiredService<IDriveOperationStore>().GetAsync(access.InstanceId, id, default))!.AuthEpoch);
    }

    [Fact]
    public async Task Proven_non_execution_is_durable_terminal_and_only_a_new_explicit_operation_can_write()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "rejected");
        var id = Guid.NewGuid();
        var rejected = await Assert.ThrowsAsync<AgentCoreException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        Assert.Equal("drive_operation_failed", rejected.Code);
        var stored = (await host.Services.GetRequiredService<IDriveOperationStore>().GetAsync(access.InstanceId, id, default))!;
        Assert.Equal("Failed", stored.State); Assert.Contains("initial_request_not_executed", stored.ResultJson);
        service = RecoveryService(host, fault); fault.Mode = "none";
        var inspection = await Assert.ThrowsAsync<AgentCoreException>(() => service.InspectOperationAsync(access, id, default).AsTask());
        Assert.Equal("drive_operation_failed", inspection.Code);
        var repeated = await Assert.ThrowsAsync<AgentCoreException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        Assert.Equal("drive_operation_failed", repeated.Code); Assert.Equal(1, fault.Uploads);
        var client = host.CreateClient(); client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var http = await client.GetAsync($"/api/v2/agent-instances/{access.InstanceId}/drive/accounts/drive/operations/{id}");
        Assert.Equal(HttpStatusCode.Conflict, http.StatusCode); Assert.Contains("drive_operation_failed", await http.Content.ReadAsStringAsync());
        var fresh = await service.EffectAsync(access, Guid.NewGuid(), "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default);
        Assert.Equal("Succeeded", fresh.Status); Assert.Equal(2, fault.Uploads);
        Assert.Single((await service.ListAsync(access, null, null, null, 20, default)).Items);
    }

    [Fact]
    public async Task A_terminal_identity_never_coalesces_into_a_later_pending_operation_with_the_same_effect()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "rejected"); var terminal = Guid.NewGuid();
        await Assert.ThrowsAsync<AgentCoreException>(() => service.EffectAsync(access, terminal, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        fault.Mode = "after"; var newer = Guid.NewGuid();
        await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, newer, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        var replay = await Assert.ThrowsAsync<AgentCoreException>(() => service.EffectAsync(access, terminal, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        Assert.Equal("drive_operation_failed", replay.Code); Assert.Equal(2, fault.Uploads);
        Assert.Equal("Indeterminate", (await host.Services.GetRequiredService<IDriveOperationStore>().GetAsync(access.InstanceId, newer, default))!.State);
        Assert.Equal("Succeeded", (await service.InspectOperationAsync(access, newer, default)).Status);
    }

    [Fact]
    public async Task Unknown_creation_and_reconnect_never_infer_failure_from_missing_metadata_or_repeat_the_write()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "before"); var id = Guid.NewGuid();
        await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        service = RecoveryService(host, fault); fault.Mode = "none";
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.InspectOperationAsync(access, id, default).AsTask());
        await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, Guid.NewGuid(), "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        var oauth = host.Services.GetRequiredService<IGoogleDriveOAuthService>();
        await oauth.DisconnectAsync(access.Credential.CredentialId, access.Credential.Revision, default);
        var current = await host.Services.GetRequiredService<CredentialService>().GetAsync(access.Credential.CredentialId);
        var profile = (await host.Services.GetRequiredService<ILocalUserProfileService>().GetLocalProfileAsync()).ProfileId;
        var start = await oauth.StartAsync(current.CredentialId, current.Revision, profile, default);
        var state = QueryHelpers.ParseQuery(start.AuthorizationUrl[start.AuthorizationUrl.IndexOf('?')..])["state"].ToString();
        await oauth.CompleteAsync(state, start.Correlation, profile, "consent", null, default);
        access = await service.ResolveAsync(access.InstanceId, "drive", null, true, default);
        Assert.Equal(3, access.Credential.DriveAccount!.AuthEpoch);
        await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, id, "drive.upload", 3, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        Assert.Equal(1, fault.Uploads);
        Assert.Equal("Indeterminate", (await host.Services.GetRequiredService<IDriveOperationStore>().GetAsync(access.InstanceId, id, default))!.State);
    }

    [Fact]
    public async Task Concurrent_duplicate_effect_waits_for_the_first_dispatch_and_reconciles_once()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "after");
        fault.Release = new(TaskCreationOptions.RunContinuationsAsynchronously); var id = Guid.NewGuid();
        var first = service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask();
        var second = service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask();
        fault.Release.SetResult();
        await Assert.ThrowsAsync<DriveIndeterminateException>(() => first);
        Assert.Equal("Succeeded", (await second).Status); Assert.Equal(1, fault.Uploads);
    }

    [Fact]
    public async Task Partial_upload_continuation_requires_the_same_snapshot_and_keeps_identity_across_service_restart()
    {
        using var host = new Factory(); var (service, access, fault) = await Recovery(host, "partial"); var id = Guid.NewGuid();
        var pending = await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default).AsTask());
        Assert.True(pending.ResumeAllowed);
        service = RecoveryService(host, fault); fault.Mode = "none";
        var read = await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.InspectOperationAsync(access, id, default).AsTask());
        Assert.True(read.ResumeAllowed); Assert.Equal(1, fault.Uploads);
        await Assert.ThrowsAsync<AgentCoreException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, "changed"u8.ToArray(), default).AsTask());
        Assert.Equal(1, fault.Uploads);
        var completed = await service.EffectAsync(access, id, "drive.upload", 1, null, RecoveryEffect, RecoveryBytes, default);
        Assert.Equal(pending.FileId, completed.File.Id); Assert.Equal(id, completed.OperationId); Assert.Equal(2, fault.Uploads);
        Assert.Single((await service.ListAsync(access, null, null, null, 20, default)).Items);
    }
}

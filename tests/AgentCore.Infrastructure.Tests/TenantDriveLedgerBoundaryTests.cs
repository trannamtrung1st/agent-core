using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantDriveLedgerBoundaryTests
{
    private static DriveAccess Access(IGoogleDriveProvider provider)
    {
        var now = DateTimeOffset.UtcNow; var owner = Guid.NewGuid(); var credential = Guid.NewGuid();
        return new(owner, new(Guid.NewGuid(), owner, credential, "drive", 1, now, now),
            new(credential, "Unbound caller", CredentialKind.Token, CredentialStatus.Active, new Dictionary<string, string>(), [], "protected", 1, 1, now, now,
                Purpose: CredentialPurpose.GoogleDriveAccount, DriveAccount: new("owned@example.test", "subject", [DriveAccountProfile.Scope], DriveConnectionStatus.Connected, 1)), provider);
    }
    [Theory] [InlineData("list")] [InlineData("get")] [InlineData("read")] [InlineData("effect")] [InlineData("inspect")] [InlineData("batch")]
    public async Task Caller_built_access_never_resolves_global_authority_or_reaches_provider_or_ledger(string operation)
    {
        var provider = new Probe(); var ledger = new LedgerProbe(); var service = new DriveService(ledger, TimeProvider.System); var access = Access(provider);
        async Task Invoke()
        {
            switch (operation)
            {
                case "list": await service.ListAsync(access, null, null, null, 1, default); break;
                case "get": await service.GetAsync(access, "file-1", default); break;
                case "read": await service.ReadAsync(access, "file-1", "1", default); break;
                case "effect": await service.EffectAsync(access, Guid.NewGuid(), "drive.create_folder", 1, null, new("Folder", "root"), ReadOnlyMemory<byte>.Empty, default); break;
                case "inspect": await service.InspectOperationAsync(access, Guid.NewGuid(), default); break;
                case "batch": await service.TrashBatchAsync(access, Guid.NewGuid(), 1, [new("file-1", "1")], default); break;
            }
        }
        Assert.Equal("tenant_drive_authority_required", (await Assert.ThrowsAsync<AccessDeniedException>(Invoke)).ReasonCode);
        Assert.Equal(0, provider.Calls); Assert.Equal(0, ledger.Calls);
    }
    [Fact]
    public async Task Revocation_during_metadata_read_withholds_result_and_keeps_the_native_ledger_untouched()
    {
        var provider = new Probe(); var ledger = new LedgerProbe(); var checks = 0;
        var access = Access(provider) with { TenantAuthority = (write, _) => {
            Assert.False(write); if (++checks > 1) throw new AccessDeniedException("source_revoked"); return ValueTask.CompletedTask; } };
        await Assert.ThrowsAsync<AccessDeniedException>(() => new DriveService(ledger, TimeProvider.System).GetAsync(access, "file-1", default).AsTask());
        Assert.Equal(2, checks); Assert.Equal(1, provider.Calls); Assert.Equal(0, ledger.Calls);
    }
    private sealed class LedgerProbe : IDriveOperationStore
    {
        public int Calls;
        private Exception Unexpected() { Calls++; return new InvalidOperationException("Unbound access reached the native ledger."); }
        public ValueTask<DriveOperation?> GetAsync(Guid owner, Guid id, CancellationToken ct) => throw Unexpected();
        public ValueTask<DriveOperation?> FindAsync(Guid owner, Guid credential, string hash, CancellationToken ct) => throw Unexpected();
        public ValueTask<DriveOperation> ClaimAsync(DriveOperation operation, CancellationToken ct) => throw Unexpected();
        public ValueTask<DriveOperation> UpdateAsync(DriveOperation operation, long expected, CancellationToken ct) => throw Unexpected();
        public ValueTask DeleteOwnerAsync(Guid owner, CancellationToken ct) => throw Unexpected();
    }
    private sealed class Probe : IGoogleDriveProvider
    {
        public int Calls;
        private Exception Unexpected() { Calls++; return new InvalidOperationException("Unbound access reached a provider."); }
        public ValueTask<DriveFile> GetAsync(string id, CancellationToken ct) { Calls++; return ValueTask.FromResult(new DriveFile(id, "Private metadata", "text/plain", 2, "1", null, ["root"], false)); }
        public ValueTask<string> GenerateIdAsync(CancellationToken ct) => throw Unexpected();
        public ValueTask<DriveFilePage> ListAsync(string? folder, string? query, string? cursor, int limit, CancellationToken ct) => throw Unexpected();
        public ValueTask CreateFolderAsync(string id, string name, string parent, CancellationToken ct) => throw Unexpected();
        public ValueTask UploadAsync(string id, string name, string parent, string mime, ReadOnlyMemory<byte> bytes, DriveOperation op, CancellationToken ct) => throw Unexpected();
        public ValueTask<byte[]> DownloadAsync(DriveFile file, CancellationToken ct) => throw Unexpected();
        public ValueTask<byte[]> DownloadAsync(DriveFile file, int max, CancellationToken ct) => throw Unexpected();
        public ValueTask RenameAsync(string id, string name, CancellationToken ct) => throw Unexpected();
        public ValueTask MoveAsync(string id, string source, string destination, CancellationToken ct) => throw Unexpected();
        public ValueTask TrashAsync(string id, CancellationToken ct) => throw Unexpected();
    }
}

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantResourceSourceContentTests
{
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Resource_source_content_keeps_binary_bytes_immutable_and_checks_exact_length(bool native)
    {
        var root = Path.Combine(Path.GetTempPath(), "resource-source-content-" + Guid.NewGuid().ToString("N"));
        try
        {
            IDefinitionResourceContentStore store = native ? new FileDefinitionResourceContentStore(root) : new InMemoryDefinitionResourceContentStore();
            byte[] bytes = [0, 255, 128, 3]; var hash = Hash(bytes); await store.StoreVerifiedAsync(hash, bytes);
            var source = (ITenantResourceSourceContentStore)store;
            var copy = await source.ReadVerifiedResourceSourceAsync(hash, bytes.LongLength); Assert.Equal(bytes, copy);
            copy![0] = 33; Assert.Equal(bytes, await source.ReadVerifiedResourceSourceAsync(hash, bytes.LongLength));
            await Assert.ThrowsAnyAsync<AgentCoreException>(() => source.ReadVerifiedResourceSourceAsync(hash, bytes.LongLength + 1).AsTask());
            if (native) Assert.False(File.Exists(Path.Combine(root, ".canonical-layout")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Resource_source_native_missing_or_invalid_reads_do_not_initialize_any_blob_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "resource-source-missing-" + Guid.NewGuid().ToString("N"));
        var store = new FileDefinitionResourceContentStore(root);
        Assert.Null(await store.ReadVerifiedResourceSourceAsync(new('a', 64), 1)); Assert.False(Directory.Exists(root));
        await Assert.ThrowsAnyAsync<AgentCoreException>(() => store.ReadVerifiedResourceSourceAsync(new('a', 64), AgentResourceLimits.MaxItemBytes + 1).AsTask());
        Assert.False(Directory.Exists(root));
    }
    [Theory] [InlineData("leaf-link")] [InlineData("root-link")] [InlineData("hard-link")]
    [InlineData("corrupt")] [InlineData("oversize")] [InlineData("truncated")]
    public async Task Resource_source_native_withholds_unsafe_corrupt_or_over_bound_blobs(string failure)
    {
        var directory = Path.Combine(Path.GetTempPath(), "resource-source-native-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "blobs");
        try
        {
            byte[] bytes = [0, 255, 128, 3]; var hash = Hash(bytes); var store = new FileDefinitionResourceContentStore(root);
            await store.StoreVerifiedAsync(hash, bytes); var file = Path.Combine(root, hash); var other = Path.Combine(directory, "outside.bin");
            if (failure == "leaf-link") { File.Move(file, other); File.CreateSymbolicLink(file, other); }
            if (failure == "root-link") { var moved = Path.Combine(directory, "moved"); Directory.Move(root, moved); Directory.CreateSymbolicLink(root, moved); }
            if (failure == "hard-link") Assert.Equal(0, LinkResourceSource(file, other));
            if (failure == "corrupt") await File.WriteAllBytesAsync(file, new byte[] { 0, 255, 128, 4 });
            if (failure == "truncated") await File.WriteAllBytesAsync(file, new byte[] { 0 });
            if (failure == "oversize") { using var large = File.OpenWrite(file); large.SetLength(AgentResourceLimits.MaxItemBytes + 1); }
            await Assert.ThrowsAnyAsync<AgentCoreException>(() => store.ReadVerifiedResourceSourceAsync(hash, bytes.LongLength).AsTask());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkResourceSource(string source, string target);
}

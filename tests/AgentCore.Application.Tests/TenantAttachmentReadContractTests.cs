using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class TenantAttachmentReadContractTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"attachmentId\":\"/private/source.txt\"}")]
    [InlineData("{\"attachmentId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"sessionId\":\"00112233-4455-6677-8899-aabbccddeeff\"}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\"}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"sheet\":null}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"offset\":-1}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"limit\":101}")]
    [InlineData("{\"attachmentId\":\"00112233-4455-6677-8899-aabbccddeeff\",\"range\":\"A1:B2\",\"cursor\":\"another-window\"}")]
    public void Read_rejects_scope_injection_host_paths_duplicates_and_invalid_windows(string json) =>
        Assert.ThrowsAny<ArgumentException>(() => TenantAttachmentReadContract.Parse(json));
}

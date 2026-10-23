using System.Net;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.Synthetic;
namespace AgentCore.Infrastructure.Tests;
public sealed partial class GoogleDriveTests
{
    [Theory]
    [InlineData(403, "drive_scope_limited")]
    [InlineData(404, "drive_not_found")]
    public async Task Trash_is_one_recoverable_patch_with_safe_known_nonexecution(int status, string code)
    {
        using var f = new Fixture(); var account = new { CredentialId = Guid.NewGuid() };
        f.Transport.FileStatus = (HttpStatusCode)status;
        var failure = await Assert.ThrowsAsync<DriveEffectNotExecutedException>(() => f.Provider(account.CredentialId).TrashAsync("file-1", default).AsTask());
        Assert.Equal(code, failure.Code); Assert.DoesNotContain("provider-secret", failure.Message);
        Assert.Equal(HttpMethod.Patch, f.Transport.LastFileMethod); Assert.Equal(1, f.Transport.FileRequests);
        using var body = JsonDocument.Parse(f.Transport.LastFileBody); Assert.True(body.RootElement.GetProperty("trashed").GetBoolean());
        Assert.DoesNotContain("delete", f.Transport.LastFilePath, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Disposable_office_fixture_stops_before_any_drive_write_for_a_real_account()
    {
        var results = Enumerable.Range(0, 9).Select(i => new ModelMessage(ModelRole.Tool, i is 5 or 6 ? "{\"artifactId\":\"fixture\"}" : i == 8 ? "{\"accounts\":[{\"available\":true,\"synthetic\":false,\"accountRef\":\"real\",\"authEpoch\":1}]}" : "{}"));
        var request = new ModelRequest(Guid.NewGuid(), [new(ModelRole.User, "synthetic-office: q3-sales-drive-trash-fixtures"), .. results], Tools: [new("drive.create_folder", "test", "{}")]);
        var output = OfficeScript.Generate(request)!; Assert.DoesNotContain(output, e => e is ModelToolCallEvent);
        Assert.Contains("no real Drive mutation", Assert.IsType<ModelTextDelta>(output[0]).Text);
    }
}

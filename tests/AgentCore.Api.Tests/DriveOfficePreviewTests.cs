using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Api.Tests;
public sealed class DriveOfficePreviewTests
{
    [Theory]
    [InlineData("docx", false, "invalid_office_package")]
    [InlineData("docx", true, "office_feature_unsupported")]
    [InlineData("xlsx", true, "office_feature_unsupported")]
    public async Task Drive_Office_problem_codes_preserve_original_download_and_source_fences(string format, bool unsupported, string code)
    {
        using var host = new AgentCoreApiFactory(); var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var credentials = host.Services.GetRequiredService<CredentialService>();
        var account = await credentials.CreateGoogleDriveAccountAsync("Preview fixture");
        var oauth = host.Services.GetRequiredService<IGoogleDriveOAuthService>();
        var ownerProfile = (await host.Services.GetRequiredService<ILocalUserProfileService>().GetLocalProfileAsync()).ProfileId;
        var start = await oauth.StartAsync(account.CredentialId, 1, ownerProfile, default);
        var state = QueryHelpers.ParseQuery(start.AuthorizationUrl[start.AuthorizationUrl.IndexOf('?')..])["state"].ToString();
        await oauth.CompleteAsync(state, start.Correlation, ownerProfile, "consent", null, default);
        var made = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 27 }); made.EnsureSuccessStatusCode();
        var owner = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("instanceId").GetGuid();
        await credentials.BindAsync(owner, account.CredentialId, "work", 1);
        var drive = host.Services.GetRequiredService<DriveService>(); var access = await drive.ResolveAsync(owner, "work", null, true, default);
        var bytes = "invalid Office package"u8.ToArray();
        if (unsupported)
        {
            var office = host.Services.GetRequiredService<IOfficeProcessor>();
            var valid = format == "docx" ? office.CreateDocument(new("Feature", [new("Section", ["Readable"], null)]), default).Bytes
                : office.CreateWorkbook(new([new("Sheet", [[new("text", Text: "Readable")]])]), default).Bytes;
            using var stream = new MemoryStream(); stream.Write(valid);
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Update, true))
            using (var writer = new StreamWriter(zip.CreateEntry(format == "docx" ? "word/embeddings/object.xml" : "xl/externalLinks/externalLink1.xml").Open())) writer.Write("<unsupported />");
            bytes = stream.ToArray();
        }
        var mime = format == "docx" ? OfficeLimits.DocumentMime : OfficeLimits.SpreadsheetMime;
        var receipt = await drive.EffectAsync(access, Guid.NewGuid(), "drive.upload", access.Credential.DriveAccount!.AuthEpoch, null,
            new("source." + format, "root", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length, mime), bytes, default);
        var path = $"/api/v2/agent-instances/{owner}/drive/accounts/work/files/{receipt.File.Id}";
        var query = $"?expectedVersion={receipt.File.Version}&authEpoch={access.Credential.DriveAccount.AuthEpoch}&bindingId={access.Binding.BindingId}";
        var preview = await client.GetAsync(path + "/office-preview" + query);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var problem = await preview.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(code, problem.GetProperty("title").GetString());
        Assert.Equal(bytes, await client.GetByteArrayAsync(path + "/content" + query));
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(path + "/office-preview" + query.Replace("expectedVersion=" + receipt.File.Version, "expectedVersion=stale"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync(path + "/office-preview" + query)).StatusCode);
    }
}

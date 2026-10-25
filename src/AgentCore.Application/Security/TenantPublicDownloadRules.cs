using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantPublicDownloadRules
{
    public static TenantPublicDownloadReceipt Read(string json)
    {
        if (json.Length > 32768) throw new ArgumentException("Invalid download receipt.");
        var value = JsonSerializer.Deserialize<TenantPublicDownloadReceipt>(json) ?? throw new ArgumentException("Invalid download receipt.");
        Validate(value); return value;
    }
    public static void Validate(TenantPublicDownloadReceipt value)
    {
        if (value.RequestedUrl is null || value.FinalUrl is null || value.Path is null || value.ContentType is null || value.Sha256 is null
            || TenantPublicDownloadContract.Url(value.RequestedUrl) != value.RequestedUrl || TenantPublicDownloadContract.Url(value.FinalUrl) != value.FinalUrl
            || value.Path.Length > 1024 || !value.Path.StartsWith("/home/", StringComparison.Ordinal) && !value.Path.StartsWith("/private/", StringComparison.Ordinal) && !value.Path.StartsWith("/working/", StringComparison.Ordinal)
            || value.ContentType.Length is < 1 or > 200 || value.ByteCount is < 0 or > int.MaxValue || value.Sha256.Length != 64
            || value.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) || value.SuggestedFileName?.Length > 120)
            throw new ArgumentException("Invalid download receipt.");
        try { TenantWorkspaceRules.ValidatePath(value.Path, file: true); }
        catch (AgentCore.Application.Sessions.AgentCoreException e) { throw new ArgumentException("Invalid download receipt path.", e); }
    }
    public static TenantOperationReceipt Attach(TenantOperationReceipt receipt, TenantWorkspaceMutationArguments args, TenantPublicDownloadReceipt? value, bool recovery)
    {
        if (value is null)
        {
            if (args.Tool == ToolCatalog.WebDownload && receipt.Outcome == TenantOperationOutcome.Completed) throw new ArgumentException("Completed downloads require factual evidence.");
            return receipt;
        }
        Validate(value); var input = TenantPublicDownloadContract.Read(args);
        var expected = input.Path ?? TenantPublicDownloadContract.Directory + "/" + receipt.OperationId.ToString("N") + "-" + TenantPublicDownloadContract.SafeName(value.SuggestedFileName, new(value.FinalUrl));
        if (recovery || receipt.Outcome != TenantOperationOutcome.Completed || args.Tool != ToolCatalog.WebDownload || input.Url.AbsoluteUri != value.RequestedUrl || expected != value.Path)
            throw new ArgumentException("Download evidence does not match the settled operation.");
        return receipt with { PublicDownload = value };
    }
    public static object? Project(TenantPublicDownloadReceipt? value) => value is null ? null : new {
        url = value.RequestedUrl, finalUrl = value.FinalUrl, path = value.Path, contentType = value.ContentType,
        byteCount = value.ByteCount, sha256 = value.Sha256, suggestedFileName = value.SuggestedFileName, untrusted = true };
}

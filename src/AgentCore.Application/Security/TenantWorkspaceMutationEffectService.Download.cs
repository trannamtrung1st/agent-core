using System.Security.Cryptography;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantWorkspaceMutationEffectService
{
    private async ValueTask<(TenantOperationOutcome Outcome, TenantPublicDownloadReceipt? Download)> Download(AgentRun run, Guid operationId,
        TenantWorkspaceMutationArguments args, Func<CancellationToken, ValueTask> check, CancellationToken ct)
    {
        if (downloads?.SupportsDownloads != true) return (TenantOperationOutcome.Denied, null);
        var input = TenantPublicDownloadContract.Read(args);
        await check(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        var result = await downloads.DownloadAsync(new(input.Url), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); await check(ct).ConfigureAwait(false);
        if (result.ErrorCode is not null) return (TenantOperationOutcome.Denied, null);
        var finalUrl = TenantPublicDownloadContract.Url(result.FinalUrl);
        var name = TenantPublicDownloadContract.SafeName(result.SuggestedFileName, new(finalUrl));
        var path = input.Path ?? TenantPublicDownloadContract.Directory + "/" + operationId.ToString("N") + "-" + name;
        var contentType = string.IsNullOrWhiteSpace(result.ContentType) ? "application/octet-stream" : result.ContentType.Split(';')[0].Trim();
        var fact = new TenantPublicDownloadReceipt(input.Url.AbsoluteUri, finalUrl, path, contentType, result.Bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(result.Bytes)), name);
        TenantPublicDownloadRules.Validate(fact);
        var owner = TenantWorkspaceEffectRules.Owner(run, path);
        if (input.Path is null)
        {
            if (!await files.TryWriteNewTenantAsync(owner, path, result.Bytes, check, ct).ConfigureAwait(false)) return (TenantOperationOutcome.Denied, null);
        }
        else await files.WriteTenantAsync(owner, path, result.Bytes, check, ct).ConfigureAwait(false);
        return (TenantOperationOutcome.Completed, fact);
    }
}

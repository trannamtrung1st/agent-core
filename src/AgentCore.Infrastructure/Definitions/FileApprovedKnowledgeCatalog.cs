using AgentCore.Application.Agents;

namespace AgentCore.Infrastructure.Definitions;

public sealed class FileApprovedKnowledgeCatalog : IApprovedKnowledgeCatalog
{
    private readonly string _directory;

    public FileApprovedKnowledgeCatalog(string agentDirectory)
    {
        _directory = Path.Combine(agentDirectory, "knowledge");
    }

    public async ValueTask<string?> ReadContentAsync(string identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (identity.IndexOfAny(['/', '\\', '.', ':']) >= 0)
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(_directory, identity + ".md"));
        var root = Path.GetFullPath(_directory);
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }
}

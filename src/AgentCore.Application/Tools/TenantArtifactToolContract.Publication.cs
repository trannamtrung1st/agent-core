using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantArtifactPublicationArguments(string DisplayName, string? ContentType, string? Content,
    string CanonicalArguments, string ParametersHash, string? SourcePath = null);

public static partial class TenantArtifactToolContract
{
    public static TenantArtifactPublicationArguments ParsePublication(PreparedArtifactPublication publication)
    {
        if (publication.Source is null)
        {
            var inline = Parse(publication.ArgumentsJson);
            return new(inline.DisplayName, inline.ContentType, inline.Content, inline.CanonicalArguments, inline.ParametersHash);
        }
        var source = TenantWorkspaceArtifactContract.Parse(publication.ArgumentsJson);
        TenantArtifactSourceRules.ValidateWitness(publication.Source, source.Path);
        return new(source.DisplayName, source.ContentType, null, source.CanonicalArguments, source.ParametersHash, source.Path);
    }
    public static bool AllowsPublication(AgentCore.Domain.Definitions.AgentDefinition definition, PreparedArtifactPublication publication) =>
        publication.Source is null ? Allows(definition) : TenantWorkspaceArtifactContract.Allows(definition);
}

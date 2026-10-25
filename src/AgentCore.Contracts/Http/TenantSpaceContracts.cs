namespace AgentCore.Contracts.Http;

public sealed record SpaceCreateRequest(Guid SpaceId, string Name, string Description, string Instructions, long ExpectedRevision);
public sealed record SpaceUpdateRequest(string Name, string Description, string Instructions, bool EnvironmentsEnabled, long ExpectedRevision, long ExpectedSpaceRevision);
public sealed record SpaceBindingDto(string Kind, string ResourceKey, string? AccountRef);
public sealed record SpaceMembershipRequest(string Responsibility, string Instructions, IReadOnlyList<string> DisabledInheritedBindingKeys,
    IReadOnlyList<SpaceBindingDto> AddedBindings, long ExpectedRevision, long ExpectedSpaceRevision, long ExpectedMembershipRevision);

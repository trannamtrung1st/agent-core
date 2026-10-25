using System.Text.Json.Serialization;

namespace AgentCore.Domain.Conversation;

/// <summary>A closed physical owner. Logical aliases never contain a host path or confer authority.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TenantWorkspaceReference.SpaceHome), "spaceHome")]
[JsonDerivedType(typeof(TenantWorkspaceReference.SessionWorking), "sessionWorking")]
public abstract record TenantWorkspaceReference
{
    private TenantWorkspaceReference(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant workspace owner is required.");
        TenantId = tenantId;
    }
    public Guid TenantId { get; }
    [JsonIgnore] public abstract Guid OwnerId { get; }
    [JsonIgnore] public abstract string LogicalRoot { get; }
    [JsonIgnore] public bool IsWorking => this is SessionWorking;
    [JsonIgnore] public Guid? WorkingSessionId => this is SessionWorking working ? working.SessionId : null;

    public sealed record SpaceHome : TenantWorkspaceReference
    {
        [JsonConstructor]
        public SpaceHome(Guid tenantId, Guid spaceId, bool privateAlias = false) : base(tenantId)
        {
            if (spaceId == Guid.Empty) throw new ArgumentException("A Space home owner is required.");
            SpaceId = spaceId; PrivateAlias = privateAlias;
        }
        public Guid SpaceId { get; }
        public bool PrivateAlias { get; }
        public override Guid OwnerId => SpaceId;
        public override string LogicalRoot => PrivateAlias ? "/private" : "/home";
    }
    public sealed record SessionWorking : TenantWorkspaceReference
    {
        [JsonConstructor]
        public SessionWorking(Guid tenantId, Guid sessionId) : base(tenantId)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A Session working owner is required.");
            SessionId = sessionId;
        }
        public Guid SessionId { get; }
        public override Guid OwnerId => SessionId;
        public override string LogicalRoot => "/working";
    }
}

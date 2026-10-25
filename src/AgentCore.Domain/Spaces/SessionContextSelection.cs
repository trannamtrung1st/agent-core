namespace AgentCore.Domain.Spaces;

/// <summary>The Session's canonical next-turn selection. A barrier never changes an already admitted Run.</summary>
public sealed record SessionContextSelection(Guid TenantId, Guid SpaceId, Guid? EnvironmentId, long EnvironmentSelectionEpoch,
    long Revision, DateTimeOffset UpdatedAt, Guid? EffectiveAfterRunId = null, Guid? TransitionId = null)
{
    public static SessionContextSelection FromCapture(RunSpaceContext capture, DateTimeOffset at, long? revision = null)
    {
        capture.Validate();
        return new(capture.TenantId, capture.SpaceId, capture.EnvironmentId, capture.EnvironmentSelectionEpoch,
            revision ?? capture.SelectionRevision, at);
    }

    public void Validate()
    {
        if (TenantId == Guid.Empty || SpaceId == Guid.Empty || EnvironmentId == Guid.Empty || Revision < 1 || EnvironmentSelectionEpoch < 0
            || EffectiveAfterRunId == Guid.Empty || TransitionId == Guid.Empty || EffectiveAfterRunId is not null && TransitionId is null
            || EnvironmentId is null && EnvironmentSelectionEpoch != 0) throw new ArgumentException("Invalid Session context selection.");
    }
}

/// <summary>Immutable interpretation/provenance. Current membership, IAM and approval still govern each protected effect.</summary>
public sealed record RunSpaceContext(Guid TenantId, Guid SpaceId, Guid PrivateSpaceId, Guid? EnvironmentId,
    long SpaceRevision, long MembershipRevision, long? EnvironmentRevision, long EnvironmentSelectionEpoch,
    string SpaceName, string SpaceInstructions, string MembershipResponsibility, string MembershipInstructions,
    bool EnvironmentsEnabled, string? EnvironmentName, long SelectionRevision)
{
    public RunSpaceWitness Witness() => new(TenantId, SpaceId, PrivateSpaceId, EnvironmentId, SpaceRevision, MembershipRevision,
        EnvironmentRevision, EnvironmentSelectionEpoch, SelectionRevision);
    public bool SameLocation(RunSpaceContext? other) => other is not null && TenantId == other.TenantId
        && SpaceId == other.SpaceId && PrivateSpaceId == other.PrivateSpaceId && EnvironmentId == other.EnvironmentId
        && EnvironmentSelectionEpoch == other.EnvironmentSelectionEpoch;

    public bool MatchesSelection(SessionContextSelection? selection) => selection is not null && TenantId == selection.TenantId
        && SpaceId == selection.SpaceId && EnvironmentId == selection.EnvironmentId
        && EnvironmentSelectionEpoch == selection.EnvironmentSelectionEpoch && SelectionRevision == selection.Revision;

    public void Validate()
    {
        if (TenantId == Guid.Empty || SpaceId == Guid.Empty || PrivateSpaceId == Guid.Empty || EnvironmentId == Guid.Empty
            || SpaceRevision < 1 || MembershipRevision < 1 || SelectionRevision < 1 || EnvironmentSelectionEpoch < 0
            || (EnvironmentId is null) != (EnvironmentRevision is null) || EnvironmentRevision is < 1
            || EnvironmentId is not null && (!EnvironmentsEnabled || string.IsNullOrWhiteSpace(EnvironmentName))
            || EnvironmentId is null && (EnvironmentName is not null || EnvironmentSelectionEpoch != 0)) throw new ArgumentException("Invalid captured Space context.");
        SpaceText.Require(SpaceName, 160, required: true); SpaceText.Require(SpaceInstructions, 16384);
        SpaceText.Require(MembershipResponsibility, 160); SpaceText.Require(MembershipInstructions, 16384);
    }
}

/// <summary>Identity and revision evidence in the canonical action hash; contains no authored instructions or values.</summary>
public sealed record RunSpaceWitness(Guid TenantId, Guid SpaceId, Guid PrivateSpaceId, Guid? EnvironmentId,
    long SpaceRevision, long MembershipRevision, long? EnvironmentRevision, long EnvironmentSelectionEpoch, long SelectionRevision)
{
    public bool MatchesSelection(SessionContextSelection? selection) => selection is not null && TenantId == selection.TenantId
        && SpaceId == selection.SpaceId && EnvironmentId == selection.EnvironmentId
        && EnvironmentSelectionEpoch == selection.EnvironmentSelectionEpoch && SelectionRevision == selection.Revision;

    public void Validate()
    {
        if (TenantId == Guid.Empty || SpaceId == Guid.Empty || PrivateSpaceId == Guid.Empty || EnvironmentId == Guid.Empty
            || SpaceRevision < 1 || MembershipRevision < 1 || SelectionRevision < 1 || EnvironmentSelectionEpoch < 0
            || (EnvironmentId is null) != (EnvironmentRevision is null) || EnvironmentRevision is < 1
            || EnvironmentId is null && EnvironmentSelectionEpoch != 0) throw new ArgumentException("Invalid operation Space witness.");
    }
}

namespace AgentCore.Application.Admin;

public sealed record InstanceVersionPreview(long InstanceRevision, int CurrentVersion, int TargetVersion,
    IReadOnlyList<string> ChangedDefinitionFields, IReadOnlyList<string> PreservedInstanceOverrides,
    IReadOnlyList<string> NewInheritedItems, IReadOnlyList<string> RemovedInheritedItems, string ActivationNotice, IReadOnlyList<string>? ChangedInheritedItems = null);

namespace AgentCore.Domain.Security;

public enum OperationApprovalMode { None, WhenRequesterLacksPermission, Always }
public enum PermissionApprovalClass { ReadOnly, Mutation, Sensitive }
public enum OperationDecisionKind { Allowed, Denied, RequiresApproval }
public enum ApprovalKind { Review, ExceptionalGrant }

public sealed record TenantOperationPolicy(Guid TenantId, string PermissionKey, OperationApprovalMode Mode,
    bool IndependentReview, long Revision);

public sealed record OperationReviewPolicy(OperationApprovalMode Mode, bool IndependentReview)
{
    public static OperationReviewPolicy Minimum(PermissionApprovalClass classification) => classification switch
    {
        PermissionApprovalClass.ReadOnly => new(OperationApprovalMode.None, false),
        PermissionApprovalClass.Mutation => new(OperationApprovalMode.WhenRequesterLacksPermission, false),
        PermissionApprovalClass.Sensitive => new(OperationApprovalMode.Always, false),
        _ => throw new ArgumentException("Unknown approval classification.")
    };
    public bool IsAtLeast(OperationReviewPolicy minimum) => Enum.IsDefined(Mode) && Mode >= minimum.Mode && (!minimum.IndependentReview || IndependentReview);
}

using AgentCore.Domain.Credentials;

namespace AgentCore.Application.Security;

public static class TenantCredentialBindingRules
{
    public static void NewBinding(AgentCredentialBinding binding)
    {
        if (binding.BindingId == Guid.Empty || binding.AgentInstanceId == Guid.Empty || binding.CredentialId == Guid.Empty
            || binding.Reference != CredentialRules.Reference(binding.Reference) || binding.Revision != 1
            || binding.CreatedAtUtc != binding.UpdatedAtUtc)
            throw new ArgumentException("Invalid initial credential binding.");
    }
    public static void Bindable(Credential value)
    {
        if (value.Status != CredentialStatus.Active) throw new AccessDeniedException("credential_disabled");
        if (value.Purpose is CredentialPurpose.ContainerRegistry or CredentialPurpose.ModelInference) throw new AccessDeniedException("credential_not_bindable");
    }
}

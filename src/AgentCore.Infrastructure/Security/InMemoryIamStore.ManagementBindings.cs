using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantManagementBindingContext> ReadTenantManagementBindingContextAsync(TenantRunReference reference, Guid? generation,
        string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => {
            var source = ReadTenantManagementSourceAsync(reference, generation, inspectionHuman, ct).Result;
            return ManagementBindingContext(source, TenantManagementMutationContract.Parse(tool, arguments), inspectionHuman, ct);
        }));
    }
    private TenantManagementBindingContext ManagementBindingContext(TenantManagementSource source, TenantManagementMutationArguments args, Guid? human, CancellationToken ct)
    {
        var reader = source.Reader; var tenant = reader.Security.Tenant.TenantId;
        TenantManagementMutationRules.Eligible(reader, args.Tool);
        var bindings = ReadTenantCredentialBindingsAsync(tenant, reader.Agent.Instance.InstanceId, ct).Result?.Bindings
            ?? throw new AccessDeniedException("management_unavailable");
        var id = args.Tool == "credentials.binding.propose" ? args.Value.GetProperty("credentialId").GetGuid()
            : bindings.SingleOrDefault(b => b.Binding.BindingId == args.Value.GetProperty("bindingId").GetGuid())?.Binding.CredentialId
                ?? throw new AccessDeniedException("credential_read_required");
        TenantManagementBindingRules.RawAsync(reader, id, human ?? reader.Agent.PrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
        var credential = FindTenantCredentialAsync(tenant, id, ct).Result ?? throw new AccessDeniedException("credential_read_required");
        var context = new TenantManagementBindingContext(source, credential, bindings.Select(b => b.Binding).ToArray());
        if (human is null) RequireManagementReadSources(reader.Run, [TenantManagementBindingRules.Inspections(context, args)[0]], ct);
        return context;
    }
}

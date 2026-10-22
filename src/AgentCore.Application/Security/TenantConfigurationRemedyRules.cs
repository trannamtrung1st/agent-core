using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// The displayed current inspection is the source of the exact settings plan, never model authority.
public static class TenantConfigurationRemedyRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static TenantManagementMutationArguments Compile(TenantManagementReader reader, TenantManagementMutationArguments requested,
        PreparedManagementRead observed)
    {
        TenantManagementMutationRules.Eligible(reader, "configuration.propose");
        var reference = TenantManagementReadService.Reference(observed, reader.Security.Tenant.TenantId);
        if (requested.Tool != "configuration.propose" || requested.ExpectedInstanceRevision != reader.Agent.Instance.Revision
            || observed.ToolName != "configuration.inspect" || observed.SessionId != reader.Run.SessionId || observed.RootSessionId != reader.Root.SessionId
            || observed.InstanceRevision != reader.Agent.Instance.Revision
            || !TenantManagementReadReferences.Read(reader.Run.Checkpoint).Contains(reference)) throw new AccessDeniedException("configuration_remedy_source_required");
        using var body = JsonDocument.Parse(observed.ObservationJson);
        var options = body.RootElement.GetProperty("data").GetProperty("remediationOptions").Deserialize<TenantConfigurationRemedy[]>(Json) ?? [];
        var option = options.SingleOrDefault(o => o.Option == requested.Value.GetProperty("option").GetString())
            ?? throw new AccessDeniedException("configuration_remedy_unavailable");
        var exact = TenantManagementMutationContract.Parse("configuration.propose", JsonSerializer.Serialize(new {
            option.Option, option.Target, option.Goal, option.Section, option.Set, option.Clear, option.ExpectedInstanceRevision }, Json));
        if (option.Operation != "settings.patch" || requested.Json != exact.Json) throw new AccessDeniedException("configuration_remedy_changed");
        var compiled = TenantManagementMutationContract.Parse("settings.patch", JsonSerializer.Serialize(new {
            option.Section, option.Set, option.Clear, option.ExpectedInstanceRevision }, Json), remedy: new(requested.Json, reference));
        return compiled with { Requested = new(requested.Tool, requested.Json) };
    }
    public static void Validate(PreparedManagementMutation value)
    {
        if (value.Remedy is not { } remedy) return;
        var requested = TenantManagementMutationContract.Parse("configuration.propose", remedy.ArgumentsJson);
        var expected = TenantManagementMutationContract.Parse("settings.patch", JsonSerializer.Serialize(new {
            expectedInstanceRevision = requested.ExpectedInstanceRevision, section = requested.Value.GetProperty("section"),
            set = requested.Value.GetProperty("set"), clear = requested.Value.GetProperty("clear") }));
        TenantManagementReadReferences.ValidateShape(remedy.Inspection);
        if (value.Harness is not null || value.Binding is not null || value.Skill is not null || value.Resource is not null
            || value.ToolName != expected.Tool || value.ArgumentsJson != expected.Json || requested.Json != remedy.ArgumentsJson
            || remedy.Inspection.ToolName != "configuration.inspect" || remedy.Inspection.InstanceRevision != requested.ExpectedInstanceRevision)
            throw new ArgumentException("Use the exact diagnosed settings remedy.");
    }
    public static bool Matches(PreparedManagementMutation value, TenantManagementMutationArguments actual) => actual.Remedy is not null
        && actual.Tool == value.ToolName && actual.Json == value.ArgumentsJson && JsonSerializer.Serialize(actual.Remedy) == JsonSerializer.Serialize(value.Remedy);
}

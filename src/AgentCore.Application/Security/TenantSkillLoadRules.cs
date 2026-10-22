using System.Globalization;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantSkillLoadRules
{
    public static void Eligible(TenantManagementReader reader)
    {
        TenantSessionRules.RequireExecutor(reader.Security, reader.Agent);
        if (!TenantTaskExecutionContract.Allows(reader.Run, reader.Current))
            throw new AccessDeniedException("skill_load_unavailable");
    }
    public static void RequireSelection(TenantManagementReader reader, IReadOnlyList<string> ids)
    {
        Eligible(reader);
        foreach (var key in ids)
            if (!reader.Run.PinnedSkillCatalog.Any(s => s.Key == key && !ManagementBuiltInSkills.IsBuiltIn(s)))
                throw new AccessDeniedException("skill_load_unavailable");
    }
    public static PreparedManagementRead Build(TenantManagementReader reader, TenantManagementReadArguments args)
    {
        Eligible(reader);
        var ids = TenantSkillLoadContract.Parse(args.Json);
        RequireSelection(reader, ids);
        var sources = new List<TenantManagementChildRead>();
        var values = new List<object>();
        foreach (var key in ids)
        {
            var pin = reader.Run.PinnedSkillCatalog.SingleOrDefault(s => s.Key == key && !ManagementBuiltInSkills.IsBuiltIn(s))
                ?? throw new AccessDeniedException("skill_unavailable");
            var item = reader.Inspection?.Items.SingleOrDefault(i => i.Kind == "skillProcedure" && i.Key == key)
                ?? throw new AccessDeniedException("skill_source_changed");
            var data = item.Metadata;
            var revision = pin.Origin == SkillOrigin.Definition ? reader.Agent.Instance.ActiveVersion
                : data.GetProperty("revision").GetInt64();
            if (!data.GetProperty("enabled").GetBoolean() || pin.SourceRevision != revision.ToString(CultureInfo.InvariantCulture)
                || data.GetProperty("name").GetString() != pin.Name || data.GetProperty("description").GetString() != pin.Description
                || data.GetProperty("procedure").GetString() != pin.Procedure || data.GetProperty("projection").GetString() != pin.Projection.ToString()
                || !data.GetProperty("requiredCapabilities").EnumerateArray().Select(c => c.GetString()).SequenceEqual(pin.RequiredCapabilities))
                throw new AccessDeniedException("skill_source_changed");
            sources.Add(new(item.Kind, item.Key, item.Fingerprint, item.Scope.ResourceId!));
            values.Add(new { pin.Key, pin.Name, pin.Description, pin.Procedure, pin.RequiredCapabilities,
                projection = pin.Projection.ToString(), pin.SourceRevision, activation = "Not activated by read consent" });
        }
        return TenantManagementReadRules.Build(reader, reader.Baseline, args, new { skills = values, procedureSource = "Pinned Run catalog", activationGranted = false }, JsonSerializer.Serialize(sources));
    }
}

using System.Text;

namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class SpaceAutomationPins
{
    private static readonly string[] NewGuardNames = ["TR_SpaceAutomationPins_Automations_INSERT", "TR_SpaceAutomationPins_Automations_UPDATE",
        "TR_SpaceAutomationPins_Occurrences_INSERT", "TR_SpaceAutomationPins_Occurrences_UPDATE", "TR_SpaceAutomationPins_Approvals_INSERT", "TR_SpaceAutomationPins_Runs_INSERT",
        "TR_SpaceAutomationPins_CoreFact_INSERT", "TR_SpaceAutomationPins_Webhook_INSERT"];
    private static readonly string[] ChangedEventGuards = ["TR_TenantCore_Fact_Insert", "TR_TenantWebhook_Receipt_Insert", "TR_TenantCore_Delivery_Insert",
        "TR_TenantCore_Delivery_Update", "TR_TenantWebhook_Delivery_Insert", "TR_TenantWebhook_Delivery_Update", "TR_TenantEvent_Bucket_Insert", "TR_TenantEvent_Bucket_Update"];

    private static string ExtendEventSnapshotGuard(string name, string previous)
    {
        if (!ChangedEventGuards.Contains(name, StringComparer.Ordinal)) return previous;
        var value = name.Contains("Bucket", StringComparison.Ordinal) ? "json_extract(NEW.PayloadJson, '$.Subscription')"
            : name is "TR_TenantCore_Fact_Insert" or "TR_TenantWebhook_Receipt_Insert" ? "f.value" : "NEW.SnapshotJson";
        var required = "'AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash'";
        var old = $"(json_type({value}, '$') IS 'object' AND (SELECT count(*) FROM json_each({value}, '$')) = 10 AND (SELECT count(DISTINCT key) FROM json_each({value}, '$') WHERE key IN ({required})) = 10)";
        if (!previous.Contains(old, StringComparison.Ordinal)) throw new InvalidOperationException("Frozen event subscription shape changed.");
        string J(string field) => $"json_extract({value}, '$.{field}')";
        string T(string field) => $"json_type({value}, '$.{field}')";
        string GuidValue(string field) => $"{T(field)}='text' AND length({J(field)})=36 AND {J(field)} NOT GLOB '*[^0-9a-f-]*' AND {J(field)}<>'00000000-0000-0000-0000-000000000000' AND substr({J(field)},9,1)='-' AND substr({J(field)},14,1)='-' AND substr({J(field)},19,1)='-' AND substr({J(field)},24,1)='-'";
        var pins = $"(({T("PinnedSpaceId")} IS NULL AND {T("PinnedEnvironmentId")} IS NULL) OR ({GuidValue("PinnedSpaceId")} AND EXISTS(SELECT 1 FROM Spaces ps WHERE lower(ps.TenantId)=lower({J("Owner.TenantId")}) AND lower(ps.SpaceId)={J("PinnedSpaceId")} AND (ps.Kind=1 OR ps.OwnerInstanceId={J("Owner.AgentInstanceId")})) AND ({T("PinnedEnvironmentId")} IS NULL OR ({GuidValue("PinnedEnvironmentId")} AND EXISTS(SELECT 1 FROM SpaceEnvironments pe WHERE lower(pe.TenantId)=lower({J("Owner.TenantId")}) AND lower(pe.SpaceId)={J("PinnedSpaceId")} AND lower(pe.EnvironmentId)={J("PinnedEnvironmentId")})))))";
        var next = $"(json_type({value}, '$') IS 'object' AND (SELECT count(*) FROM json_each({value}, '$')) = (10 + CASE WHEN {T("PinnedSpaceId")} IS NULL THEN 0 ELSE 1 END + CASE WHEN {T("PinnedEnvironmentId")} IS NULL THEN 0 ELSE 1 END) AND (SELECT count(DISTINCT key) FROM json_each({value}, '$') WHERE key IN ({required})) = 10 AND {pins})";
        return previous.Replace(old, next, StringComparison.Ordinal);
    }
    private static string Available(string tenant, string instance, string space, string environment) => $"""
        EXISTS(SELECT 1 FROM Spaces s JOIN SpaceMemberships m ON m.TenantId=s.TenantId AND m.SpaceId=s.SpaceId
            JOIN SystemIdentities i ON i.TenantId=m.TenantId AND i.AgentInstanceId=m.InstanceId AND i.Status=0
            WHERE s.TenantId={tenant} AND lower(s.SpaceId)=lower({space}) AND s.DeletedAt IS NULL AND m.InstanceId={instance}
                AND (s.Kind=1 OR s.OwnerInstanceId={instance}) AND ({environment} IS NULL OR (s.EnvironmentsEnabled=1
                    AND EXISTS(SELECT 1 FROM SpaceEnvironments e WHERE e.TenantId=s.TenantId AND e.SpaceId=s.SpaceId
                        AND lower(e.EnvironmentId)=lower({environment}) AND e.DeletedAt IS NULL))))
        """;
    private static string PinGuards()
    {
        var sql = new StringBuilder();
        var available = Available("NEW.TenantId", "NEW.AgentInstanceId", "NEW.PinnedSpaceId", "NEW.PinnedEnvironmentId");
        foreach (var action in new[] { "INSERT", "UPDATE" })
        {
            var changed = action == "INSERT" ? "1" : "NEW.PinnedSpaceId IS NOT OLD.PinnedSpaceId OR NEW.PinnedEnvironmentId IS NOT OLD.PinnedEnvironmentId OR NEW.Status=0 AND OLD.Status<>0";
            sql.AppendLine($"CREATE TRIGGER TR_SpaceAutomationPins_Automations_{action} BEFORE {action} ON Automations WHEN NEW.PinnedSpaceId IS NOT NULL AND ({changed}) BEGIN SELECT RAISE(ABORT,'Pinned Automation context is unavailable') WHERE NOT ({available}); END;");
        }
        var matchesRun = """
            json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.spaceId') IS coalesce(lower(NEW.PinnedSpaceId),json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.privateSpaceId'))
            AND json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.environmentId') IS lower(NEW.PinnedEnvironmentId)
            """;
        sql.AppendLine($"""
            CREATE TRIGGER TR_SpaceAutomationPins_Occurrences_INSERT BEFORE INSERT ON TriggerOccurrences WHEN NEW.TenantId IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT,'Occurrence pins differ from their saved source') WHERE NEW.AutomationId IS NOT NULL AND NOT EXISTS(
                    SELECT 1 FROM Automations a WHERE a.AutomationId=NEW.AutomationId AND a.TenantId=NEW.TenantId
                        AND a.PinnedSpaceId IS NEW.PinnedSpaceId AND a.PinnedEnvironmentId IS NEW.PinnedEnvironmentId);
                SELECT RAISE(ABORT,'Occurrence pins differ from their admitted Run') WHERE EXISTS(SELECT 1 FROM AgentRuns r
                    WHERE r.AgentRunId=NEW.AcceptedAgentRunId AND NOT ({matchesRun}));
            END;
            CREATE TRIGGER TR_SpaceAutomationPins_Occurrences_UPDATE BEFORE UPDATE ON TriggerOccurrences WHEN NEW.TenantId IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT,'Occurrence context pins are immutable') WHERE NEW.PinnedSpaceId IS NOT OLD.PinnedSpaceId OR NEW.PinnedEnvironmentId IS NOT OLD.PinnedEnvironmentId;
                SELECT RAISE(ABORT,'Occurrence pins differ from their admitted Run') WHERE EXISTS(SELECT 1 FROM AgentRuns r
                    WHERE r.AgentRunId=NEW.AcceptedAgentRunId AND NOT ({matchesRun}));
            END;
            CREATE TRIGGER TR_SpaceAutomationPins_Runs_INSERT BEFORE INSERT ON AgentRuns WHEN NEW.TenantId IS NOT NULL
                AND json_type(NEW.PayloadJson,'$.admission.activation.triggerOccurrenceId')='text'
            BEGIN
                SELECT RAISE(ABORT,'Run context differs from its frozen occurrence pins') WHERE EXISTS(SELECT 1 FROM TriggerOccurrences o
                    WHERE o.OccurrenceId=json_extract(NEW.PayloadJson,'$.admission.activation.triggerOccurrenceId') AND o.TenantId=NEW.TenantId
                        AND (json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.spaceId') IS NOT coalesce(lower(o.PinnedSpaceId),json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.privateSpaceId'))
                            OR json_extract(NEW.PayloadJson,'$.admission.configuration.spaceContext.environmentId') IS NOT lower(o.PinnedEnvironmentId)));
            END;
            """);
        sql.AppendLine("CREATE TRIGGER TR_SpaceAutomationPins_Approvals_INSERT BEFORE INSERT ON TenantApprovalRequests BEGIN");
        foreach (var path in new[] { "AutomationAdmission.Current", "AutomationAdmission.Next", "AutomationMutation.Current", "AutomationMutation.Proposed" })
        {
            var root = "'$.Operation." + path + "'";
            var space = "json_extract(NEW.PayloadJson,'$.Operation." + path + ".PinnedSpaceId')";
            var environment = "json_extract(NEW.PayloadJson,'$.Operation." + path + ".PinnedEnvironmentId')";
            var version = "json_extract(NEW.PayloadJson,'$.Operation." + path + ".SchemaVersion')";
            sql.AppendLine($"SELECT RAISE(ABORT,'Prepared context pins require their closed consent schema') WHERE json_type(NEW.PayloadJson,{root})='object' AND NOT COALESCE((({space} IS NULL AND {environment} IS NULL AND {version} IS NOT 5) OR ({version}=5 AND json_type(NEW.PayloadJson,'$.Operation.{path}.PinnedSpaceId')='text' AND length({space})=36 AND {space}<>'00000000-0000-0000-0000-000000000000' AND ({environment} IS NULL OR (json_type(NEW.PayloadJson,'$.Operation.{path}.PinnedEnvironmentId')='text' AND length({environment})=36 AND {environment}<>'00000000-0000-0000-0000-000000000000')))),0);");
        }
        sql.AppendLine("""
            SELECT RAISE(ABORT,'Prepared occurrence pins must match its exact context') WHERE json_type(NEW.PayloadJson,'$.Operation.AutomationAdmission')='object' AND NOT COALESCE(
                json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId') IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedSpaceId')
                AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId') IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Next.PinnedEnvironmentId')
                AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId') IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Occurrence.PinnedSpaceId')
                AND json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId') IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Occurrence.PinnedEnvironmentId')
                AND json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.SpaceId') IS coalesce(json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId'),json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.PrivateSpaceId'))
                AND json_extract(NEW.PayloadJson,'$.Operation.SpaceContext.EnvironmentId') IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId'),0);
            SELECT RAISE(ABORT,'Prepared context pins differ from their retained source') WHERE json_type(NEW.PayloadJson,'$.Operation.AutomationAdmission')='object' AND (
                NOT EXISTS(SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AutomationId=NEW.AutomationId
                    AND lower(a.PinnedSpaceId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId')
                    AND lower(a.PinnedEnvironmentId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId'))
                OR NOT EXISTS(SELECT 1 FROM TriggerOccurrences o WHERE o.TenantId=NEW.TenantId AND o.OccurrenceId=json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Occurrence.OccurrenceId')
                    AND lower(o.PinnedSpaceId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId')
                    AND lower(o.PinnedEnvironmentId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedEnvironmentId')));
            SELECT RAISE(ABORT,'Prepared authoring source pins changed') WHERE json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Current')='object' AND NOT EXISTS(
                SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND lower(a.AutomationId)=json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.AutomationId')
                    AND lower(a.PinnedSpaceId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedSpaceId')
                    AND lower(a.PinnedEnvironmentId) IS json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.PinnedEnvironmentId'));
            END;
            """);
        foreach (var (table, name) in new[] { ("CoreEvents", "TR_SpaceAutomationPins_CoreFact_INSERT"), ("ExternalEvents", "TR_SpaceAutomationPins_Webhook_INSERT") })
            sql.AppendLine($"CREATE TRIGGER {name} BEFORE INSERT ON {table} WHEN NEW.TenantId IS NOT NULL BEGIN SELECT RAISE(ABORT,'Event receipt pins differ from their saved subscription') WHERE EXISTS(SELECT 1 FROM json_each(NEW.SubscriptionsJson) f WHERE NOT EXISTS(SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AutomationId=json_extract(f.value,'$.AutomationId') AND lower(a.PinnedSpaceId) IS json_extract(f.value,'$.PinnedSpaceId') AND lower(a.PinnedEnvironmentId) IS json_extract(f.value,'$.PinnedEnvironmentId'))); END;");
        return sql.ToString();
    }
}

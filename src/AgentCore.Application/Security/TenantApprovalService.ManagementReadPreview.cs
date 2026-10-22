using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    private static IReadOnlyList<TenantApprovalFieldChange> ManagementReadObservationChanges(TenantManagementReadObservation observed)
    {
        if (observed.SourcesChanged) return [new("Current observation", null, "The saved configuration changed or management was revoked. Current values are withheld. Cancel this request and inspect a fresh observation.", false)];
        using var document = JsonDocument.Parse(observed.Value.ObservationJson); var root = document.RootElement;
        var result = new List<TenantApprovalFieldChange> { new("Observed Agent revision", null, root.GetProperty("instanceRevision").ToString(), false),
            new("Changes apply to", null, "The next new Run; the current Run stays pinned.", false) };
        var data = root.GetProperty("data");
        static string Text(JsonElement value) => value.ValueKind == JsonValueKind.Null ? "Inherit Agent default" : value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString();
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var section in data.EnumerateArray()) result.Add(new(section.GetProperty("section").GetString()!, null,
                section.GetProperty("hasOverrides").GetBoolean() ? "Instance overrides present" : "Inherits Definition defaults", false));
        }
        else if (data.TryGetProperty("effective", out var effective) && root.GetProperty("tool").GetString() == "settings.inspect")
        {
            foreach (var field in effective.EnumerateObject())
            {
                var source = data.GetProperty("sources").TryGetProperty(field.Name, out var origin) ? origin.GetString() : "observed";
                result.Add(new(field.Name, null, Text(field.Value) + "\nSource: " + source, false));
            }
        }
        else foreach (var field in data.EnumerateObject()) result.Add(new(System.Text.RegularExpressions.Regex.Replace(field.Name, "(?<!^)[A-Z]", " $0"), null, Text(field.Value), false));
        if (root.GetProperty("excerptFields").GetArrayLength() > 0)
            result.Add(new("Bounded excerpts", null, "Some values are excerpts; do not treat them as complete replacement settings. " + string.Join(", ", root.GetProperty("excerptFields").EnumerateArray().Select(v => v.GetString())), false));
        return result;
    }
}

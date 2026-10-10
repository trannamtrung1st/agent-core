using MessagePack;
using MessagePack.Formatters;
namespace AgentCore.Contracts.Realtime;

// Preserve schema errors for the normal command rejection instead of silently dropping unknown MessagePack fields.
public sealed class UserMessagePartFormatter : IMessagePackFormatter<UserMessagePartDto?>
{
    public void Serialize(ref MessagePackWriter w, UserMessagePartDto? value, MessagePackSerializerOptions options)
    {
        if (value is null) { w.WriteNil(); return; }
        w.WriteMapHeader(6);
        w.Write("kind"); w.Write(value.Kind); w.Write("text"); w.Write(value.Text);
        w.Write("invocationKind"); w.Write(value.InvocationKind); w.Write("skillKey"); w.Write(value.SkillKey);
        w.Write("reference"); options.Resolver.GetFormatterWithVerify<UserResourceReferenceDto>().Serialize(ref w, value.Reference!, options);
        w.Write("label"); w.Write(value.Label);
    }
    public UserMessagePartDto? Deserialize(ref MessagePackReader r, MessagePackSerializerOptions options)
    {
        if (r.TryReadNil()) return null!;
        options.Security.DepthStep(ref r);
        try
        {
            var result = new UserMessagePartDto(); var seen = new HashSet<string>(StringComparer.Ordinal);
            var count = r.ReadMapHeader();
            for (var i = 0; i < count; i++)
            {
                var key = r.ReadString() ?? ""; if (!seen.Add(key)) result.InvalidFields = true;
                switch (key)
                {
                    case "kind": result.Kind = r.ReadString() ?? ""; break;
                    case "text": result.Text = r.ReadString(); break;
                    case "invocationKind": result.InvocationKind = r.ReadString(); break;
                    case "skillKey": result.SkillKey = r.ReadString(); break;
                    case "reference": result.Reference = options.Resolver.GetFormatterWithVerify<UserResourceReferenceDto>().Deserialize(ref r, options); break;
                    case "label": result.Label = r.ReadString(); break;
                    default: result.InvalidFields = true; r.Skip(); break;
                }
            }
            return result;
        }
        finally { r.Depth--; }
    }
}
public sealed class UserResourceReferenceFormatter : IMessagePackFormatter<UserResourceReferenceDto?>
{
    public void Serialize(ref MessagePackWriter w, UserResourceReferenceDto? value, MessagePackSerializerOptions options)
    {
        if (value is null) { w.WriteNil(); return; }
        w.WriteMapHeader(8);
        w.Write("kind"); w.Write(value.Kind); w.Write("agentInstanceId"); w.Write(value.AgentInstanceId);
        w.Write("sessionId"); w.Write(value.SessionId); w.Write("itemId"); w.Write(value.ItemId);
        w.Write("artifactId"); w.Write(value.ArtifactId); w.Write("agentRunId"); w.Write(value.AgentRunId);
        w.Write("skillKey"); w.Write(value.SkillKey); w.Write("selectedRevision");
        if(value.SelectedRevision is { } revision) w.Write(revision); else w.WriteNil();
    }
    public UserResourceReferenceDto? Deserialize(ref MessagePackReader r, MessagePackSerializerOptions options)
    {
        if (r.TryReadNil()) return null!;
        options.Security.DepthStep(ref r);
        try
        {
            var result = new UserResourceReferenceDto(); var seen = new HashSet<string>(StringComparer.Ordinal);
            var count = r.ReadMapHeader();
            for (var i = 0; i < count; i++)
            {
                var key = r.ReadString() ?? ""; if (!seen.Add(key)) result.InvalidFields = true;
                switch(key)
                {
                    case "kind": result.Kind = r.ReadString() ?? ""; break;
                    case "agentInstanceId": result.AgentInstanceId = r.ReadString(); break;
                    case "sessionId": result.SessionId = r.ReadString(); break;
                    case "itemId": result.ItemId = r.ReadString(); break;
                    case "artifactId": result.ArtifactId = r.ReadString(); break;
                    case "agentRunId": result.AgentRunId = r.ReadString(); break;
                    case "skillKey": result.SkillKey = r.ReadString(); break;
                    case "selectedRevision": result.SelectedRevision = r.TryReadNil() ? null : r.ReadInt64(); break;
                    default: result.InvalidFields = true; r.Skip(); break;
                }
            }
            return result;
        }
        finally { r.Depth--; }
    }
}

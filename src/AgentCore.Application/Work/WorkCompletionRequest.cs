using System.Text.Json;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Work;

public static class WorkCompletionRequest
{
    public static bool TryParse(
        JsonElement args,
        out string summary,
        out bool attentionRequired,
        out string rejection)
    {
        summary = "";
        attentionRequired = false;
        rejection = "Completion arguments are invalid.";
        if (args.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? text = null;
        bool? attention = null;
        foreach (var property in args.EnumerateObject())
        {
            if (property.NameEquals("summary"))
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    rejection = "Completion summary must be text.";
                    return false;
                }

                text = property.Value.GetString();
                continue;
            }

            if (property.NameEquals("attentionRequired"))
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    rejection = "attentionRequired must be true or false.";
                    return false;
                }

                attention = property.Value.GetBoolean();
                continue;
            }

            rejection = "The model cannot choose a recipient.";
            return false;
        }

        if (text is null || attention is null)
        {
            rejection = "Completion requires summary and attentionRequired.";
            return false;
        }

        try
        {
            summary = new WorkResult(text, DateTimeOffset.UnixEpoch, attention.Value).Text;
        }
        catch (ArgumentException exception)
        {
            rejection = exception.Message;
            return false;
        }

        attentionRequired = attention.Value;
        return true;
    }
}

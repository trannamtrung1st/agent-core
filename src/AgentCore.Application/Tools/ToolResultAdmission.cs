using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class ToolResultAdmission
{
    private static readonly string VisionRequiredJson = JsonSerializer.Serialize(new
    {
        error = "vision_required",
        message = "This model cannot inspect image content returned by attachments.read."
    });

    public static ToolExecutionResult AdmitForModel(ILanguageModel model, ToolExecutionResult result)
    {
        if (result.Parts is not { Count: > 0 } parts
            || !parts.Any(part => part is ModelImageContent))
        {
            return result;
        }

        if (model.Capabilities.Vision)
        {
            return result;
        }

        return ToolExecutionResult.FromText(VisionRequiredJson);
    }
}

using AgentCore.Application.Ports;
using System.Text.Json;

namespace AgentCore.Infrastructure.Providers.SemanticResponses;

internal static class AssistantResponseSchema
{
    public const int MaxJsonCharacters = 128 * 1024;
    public const int MaxDisplayCharacters = 64 * 1024;
    public const int MaxSpeechCharacters = 8 * 1024;
    public const int MaxBlocks = 32;
    public const int MaxBlockCharacters = 16 * 1024;

    public const string CompatibilityInstructionPrefix =
        "Infrastructure output format: write the visible answer as ordinary text. ";

    public static string CompatibilityInstruction(ModelResponseContract contract)
    {
        var speech = contract.SpeechWillBeUsed
            ? "A spoken projection may be needed; omit [[speech:...]] when the display is natural to say aloud. "
            : "Omit [[speech:...]] unless spoken wording must differ from the display. ";
        return CompatibilityInstructionPrefix
            + speech
            + "Optional custom speech uses [[speech:<spoken text>]] immediately before display text. "
            + "Optional rich blocks use [[md:...]], [[attachment:<id>]], or [[artifact:<id>]]. "
            + "Do not include hidden reasoning. Do not emit JSON.";
    }

    public const string SchemaName = "agent_core_assistant_response";

    private const string JsonSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["displayText", "speech", "blocks"],
          "properties": {
            "displayText": {
              "type": "string",
              "description": "Visible conversational answer shown to the user."
            },
            "speech": {
              "type": "object",
              "additionalProperties": false,
              "required": ["mode", "text"],
              "properties": {
                "mode": {
                  "type": "string",
                  "enum": ["same", "custom", "none"],
                  "description": "same: spoken wording follows displayText. custom: text is required alternate spoken wording. none: intentionally visual-only; do not speak. Rich blocks are never automatically spoken."
                },
                "text": {
                  "type": ["string", "null"],
                  "description": "Required concise spoken wording when mode is custom; otherwise null."
                }
              }
            },
            "blocks": {
              "type": "array",
              "description": "Optional richer visual blocks. Never automatically spoken.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["kind", "text", "attachmentId", "artifactId"],
                "properties": {
                  "kind": {
                    "type": "string",
                    "enum": ["markdown", "attachmentReference", "artifactReference"]
                  },
                  "text": {
                    "type": ["string", "null"],
                    "description": "Markdown or caption text; required for markdown blocks."
                  },
                  "attachmentId": {
                    "type": ["string", "null"],
                    "description": "Session attachment id when kind is attachmentReference."
                  },
                  "artifactId": {
                    "type": ["string", "null"],
                    "description": "Session artifact id when kind is artifactReference."
                  }
                }
              }
            }
          }
        }
        """;

    public static object OpenAiCompatibleResponseFormat { get; } = new Dictionary<string, object?>
    {
        ["type"] = "json_schema",
        ["json_schema"] = new Dictionary<string, object?>
        {
            ["name"] = SchemaName,
            ["strict"] = true,
            ["schema"] = JsonSerializer.Deserialize<JsonElement>(JsonSchemaJson)!
        }
    };
}

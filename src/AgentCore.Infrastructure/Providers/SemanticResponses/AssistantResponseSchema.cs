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

    public static string CompatibilityInstruction(ModelResponseContract contract, bool responseFunction = false)
    {
        var speech = contract.SpeechWillBeUsed
            ? "A spoken projection may be needed; omit [[speech:...]] when the display is natural to say aloud. "
            : "Omit [[speech:...]] unless spoken wording must differ from the display. ";
        var memory = responseFunction
            ? $"Call {ResponseFunctionName} with the structured answer. That call is the reliable memory proposal channel. [[memory:...]] alone is best effort. "
            : "[[memory:...]] is best effort only. Do not claim that memory was saved. ";
        return CompatibilityInstructionPrefix
            + speech
            + "Optional custom speech uses [[speech:<spoken text>]] immediately before display text. "
            + "Use [[speech:none]] when the answer must stay visual-only. "
            + "Optional rich blocks use [[md:...]], [[attachment:<id>]], or [[artifact:<id>]]. "
            + "Do not include hidden reasoning. Do not emit JSON. "
            + "This plain-text channel is Complete with one chat.respond. "
            + memory;
    }

    public const string ResponseFunctionName = "agent_core_respond";

    public static ModelToolDefinition ResponseFunction { get; } = new(
        ResponseFunctionName,
        "Submit one agent step for the current session. disposition is Continue, Wait, Complete, or Blocked. action is {\"kind\":\"chat.respond\"} or null. Continue does not start another generation. Memory entries are proposals. This call does not save memory and does not choose a destination.",
        JsonSchemaJson);

    public const string SchemaName = "agent_core_assistant_response";

    internal const string JsonSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["disposition", "action", "displayText", "speech", "blocks", "memory"],
          "properties": {
            "disposition": {
              "type": "string",
              "enum": ["Continue", "Wait", "Complete", "Blocked"],
              "description": "Continue delivers chat.respond once when that action is present and does not start another generation. Wait returns control and does not deliver Chat. Complete finishes this activation. Blocked means this activation cannot succeed."
            },
            "action": {
              "description": "The single requested application action, or null when this step requests no effect. Only chat.respond is valid. Do not include a session, destination, profile, tenant, or recipient.",
              "anyOf": [
                { "type": "null" },
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["kind"],
                  "properties": {
                    "kind": {
                      "type": "string",
                      "enum": ["chat.respond"],
                      "description": "Deliver displayText, speech, and blocks to the current session."
                    }
                  }
                }
              ]
            },
            "displayText": {
              "type": "string",
              "description": "Visible conversational answer when action is chat.respond. Use an empty string when action is null."
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
            "memory": {
              "type": "array",
              "description": "Optional learned-memory proposals. Empty when nothing should be remembered. The runtime admits or rejects each item; display text must not claim a save.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["operation", "kind", "subject", "content", "scopeHint", "source"],
                "properties": {
                  "operation": { "type": "string", "enum": ["upsert", "delete"] },
                  "kind": { "type": "string", "enum": ["fact", "preference", "goal", "decision", "openLoop"] },
                  "subject": { "type": "string" },
                  "content": { "type": "string" },
                  "scopeHint": { "type": ["string", "null"], "enum": ["session", "identityUser", "user", null] },
                  "source": { "type": "string", "enum": ["userExplicit", "agentInferred"] }
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

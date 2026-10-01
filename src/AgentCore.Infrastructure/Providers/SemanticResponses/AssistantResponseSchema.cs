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
        var agentStep = responseFunction
            ? contract.RequireChatResponse
                ? "This is a direct user chat request: finish with disposition Complete or Continue and action chat.respond with non-empty displayText (never whitespace-only). action null and Wait are not valid for this request. Continue delivers chat.respond once when present and does not start another generation. "
                : "For a normal user request: use available tools and Skills as needed; finish with disposition Complete and action chat.respond. chat.respond MUST include non-empty displayText (never whitespace-only). Use action null only when this step intentionally delivers no Chat. Wait only for a real external waiting condition—not because work is long, difficult, or unfinished. Continue does not request another model turn. "
            : string.Empty;
        var terminal = contract.RequireChatResponse
            ? "This plain-text channel requires Complete or Continue with one chat.respond. "
            : "This plain-text channel is Complete with one chat.respond. ";
        return CompatibilityInstructionPrefix
            + speech
            + "Optional custom speech uses [[speech:<spoken text>]] immediately before display text. "
            + "Use [[speech:none]] when the answer must stay visual-only. "
            + "Optional rich blocks use [[md:...]], [[attachment:<id>]], or [[artifact:<id>]]. "
            + "Do not include hidden reasoning. Do not emit JSON. "
            + terminal
            + agentStep
            + memory;
    }

    public const string ResponseFunctionName = "agent_core_respond";

    public static ModelToolDefinition ResponseFunction(ModelResponseContract contract) => new(
        ResponseFunctionName,
        ResponseFunctionDescription(contract),
        JsonSchemaFor(contract));

    public static ModelToolDefinition DefaultResponseFunction =>
        ResponseFunction(new ModelResponseContract(SpeechWillBeUsed: false));

    public const string SchemaName = "agent_core_assistant_response";

    public static string JsonSchemaJson => JsonSchemaFor(new ModelResponseContract(SpeechWillBeUsed: false));

    public static string JsonSchemaFor(ModelResponseContract contract) =>
        contract.RequireChatResponse ? DirectUserTurnJsonSchemaJson : GeneralJsonSchemaJson;

    internal const string GeneralJsonSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["disposition", "action", "displayText", "speech", "blocks", "memory"],
          "properties": {
            "disposition": {
              "type": "string",
              "enum": ["Continue", "Wait", "Complete", "Blocked"],
              "description": "For ordinary user chat: prefer Complete. Continue delivers chat.respond once when that action is present and does not start another generation; it does not invoke another model turn or buy more thinking time. Wait returns control without Chat only when an external condition must arrive first; never use Wait because work is long, difficult, needs tools, or is unfinished. Complete finishes this activation. Blocked means this activation cannot succeed."
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
                      "description": "Deliver a non-empty displayText answer, speech, and blocks to the current session. Invalid with empty or whitespace-only displayText."
                    }
                  }
                }
              ]
            },
            "displayText": {
              "type": "string",
              "description": "Visible conversational answer. REQUIRED non-empty text when action is chat.respond; empty or whitespace-only displayText with chat.respond is invalid. Use an empty string only when action is null."
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

    internal const string DirectUserTurnJsonSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["disposition", "action", "displayText", "speech", "blocks", "memory"],
          "properties": {
            "disposition": {
              "type": "string",
              "enum": ["Continue", "Complete"],
              "description": "Direct user chat: Complete finishes this answer; Continue delivers chat.respond once when present and does not start another generation."
            },
            "action": {
              "type": "object",
              "additionalProperties": false,
              "required": ["kind"],
              "properties": {
                "kind": {
                  "type": "string",
                  "enum": ["chat.respond"],
                  "description": "Required for direct user chat. Deliver non-empty displayText to the current session."
                }
              }
            },
            "displayText": {
              "type": "string",
              "minLength": 1,
              "description": "Required visible answer for direct user chat. Must not be empty or whitespace-only."
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

    private static string ResponseFunctionDescription(ModelResponseContract contract) =>
        contract.RequireChatResponse
            ? "Submit the agent step for a direct user chat request. disposition must be Continue or Complete. action must be {\"kind\":\"chat.respond\"} with non-empty displayText. Wait, Blocked, and action null are invalid for this request. Memory entries are proposals."
            : "Submit one agent step for the current session. For a normal user request: use tools and Skills as needed, then Complete with action {\"kind\":\"chat.respond\"} and a non-empty displayText answer; never call chat.respond with empty or whitespace-only displayText. disposition is Continue, Wait, Complete, or Blocked. action is {\"kind\":\"chat.respond\"} or null. Continue delivers chat.respond once when present and does not start another generation; it does not request more model thinking. Wait returns control without Chat only when an external condition must arrive first—not because a task is long, difficult, needs tools, or is unfinished. action null means no Chat this step. Memory entries are proposals. This call does not save memory and does not choose a destination.";

    public static object OpenAiCompatibleResponseFormat(ModelResponseContract contract) =>
        new Dictionary<string, object?>
        {
            ["type"] = "json_schema",
            ["json_schema"] = new Dictionary<string, object?>
            {
                ["name"] = SchemaName,
                ["strict"] = true,
                ["schema"] = JsonSerializer.Deserialize<JsonElement>(JsonSchemaFor(contract))!
            }
        };

    public static object DefaultOpenAiCompatibleResponseFormat =>
        OpenAiCompatibleResponseFormat(new ModelResponseContract(SpeechWillBeUsed: false));
}

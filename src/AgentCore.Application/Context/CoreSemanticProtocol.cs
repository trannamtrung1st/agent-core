using AgentCore.Application.Ports;
namespace AgentCore.Application.Context;
internal static class CoreSemanticProtocol
{
    public const string CompatibilityInstructionPrefix = "Infrastructure output format: ";

    public static string CompatibilityInstruction(ModelResponseContract contract, bool responseFunction = false) =>
        responseFunction ? ResponseFunctionInstruction(contract) : MarkerCompatibilityInstruction(contract);

    private static string ResponseFunctionInstruction(ModelResponseContract contract) =>
        CompatibilityInstructionPrefix
        + "Call agent_core_respond for the terminal semantic response. "
        + "Put rich content in blocks, speech in speech, and memory proposals in memory. "
        + "Represent artifacts with a typed artifactReference block containing the returned artifactId. "
        + "displayText is ordinary display text; never place compatibility control markers in displayText or ordinary output. "
        + "Do not include hidden reasoning. "
        + ResponseFunctionDescription(contract);

    private static string MarkerCompatibilityInstruction(ModelResponseContract contract)
    {
        var speech = contract.SpeechWillBeUsed
            ? "A spoken projection may be needed; omit [[speech:...]] when the display is natural to say aloud. "
            : "Omit [[speech:...]] unless spoken wording must differ from the display. ";
        var terminal = contract.RequireChatResponse
            ? "This plain-text channel requires Complete or Continue with one chat.respond. "
            : "This plain-text channel is Complete with one chat.respond. ";
        return CompatibilityInstructionPrefix
            + "write the visible answer as ordinary text. "
            + speech
            + "Optional custom speech uses [[speech:<spoken text>]] immediately before display text. "
            + "Use [[speech:none]] when the answer must stay visual-only. "
            + "Rich blocks: [[md:...]], [[attachment:<id>]], [[artifact:<id>]]. "
            + ArtifactReferenceInstruction + " "
            + "Do not include hidden reasoning. Do not emit JSON. "
            + terminal
            + "[[memory:...]] is best effort only. Do not claim that memory was saved. ";
    }


    private static string ResponseFunctionDescription(ModelResponseContract contract)
    {
        var speech = contract.SpeechWillBeUsed
            ? " speech is required. Normally use {\"mode\":\"same\",\"text\":null}. Use {\"mode\":\"none\",\"text\":null} only for visual-only output. Use {\"mode\":\"custom\",\"text\":\"...\"} only when spoken wording must differ from displayText."
            : " For a text turn, speech must be {\"mode\":\"same\",\"text\":null}.";
        speech += " " + ArtifactReferenceInstruction;
        return contract.RequireChatResponse
            ? "Submit the agent step for a direct user chat request. disposition must be Continue or Complete. action must be {\"kind\":\"chat.respond\"} with non-empty displayText. Wait, Blocked, and action null are invalid for this request. Memory entries are proposals." + speech
            : "Submit one agent step for the current session. For a normal user request: use tools and Skills as needed, then Complete with action {\"kind\":\"chat.respond\"} and a non-empty displayText answer; never call chat.respond with empty or whitespace-only displayText. disposition is Continue, Wait, Complete, or Blocked. action is {\"kind\":\"chat.respond\"} or null. Continue delivers chat.respond once when present and does not start another generation; it does not request more model thinking. Wait returns control without Chat only when an external condition must arrive first—not because a task is long, difficult, needs tools, or is unfinished. action null means no Chat this step. Memory entries are proposals. This call does not save memory and does not choose a destination." + speech;
    }


    public const string ArtifactReferenceInstruction =
        "Artifact IDs must come from successful artifacts.create or artifacts.create_from_workspace for this Session. Workspace paths/filenames are not artifact IDs; materialize the file first or use plain text.";
}

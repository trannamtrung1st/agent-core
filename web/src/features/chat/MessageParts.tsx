import { displayPart, messageText, type MessagePart } from "../../services/messageParts";

type ChipPart = Exclude<MessagePart, { kind: "text" }>;
export type MessagePartContext = "draft" | "queue" | "history";

const referenceLabels = {
  homeFile: "File",
  session: "Chat",
  backgroundSession: "Background work",
  artifact: "Artifact",
  agentRun: "Run",
  skill: "Skill reference",
};

// Presentation is shared with Lexical; canonical parts remain untouched.
export function chipPresentation(part: ChipPart, context: MessagePartContext) {
  const kind = part.kind === "invocation" ? "invocation" : part.reference.kind;
  const title = part.kind === "invocation"
    ? `Skill · ${part.skillKey}${context === "history" ? " · invoked in this message" : " · activates when sent"}`
    : `${referenceLabels[part.reference.kind]} · ${messageText([part])}`;
  return { className: `composer-chip composer-chip-${part.kind}`, title, kind };
}

export function MessageParts({ parts, context }: {
  parts: readonly MessagePart[];
  context: MessagePartContext;
}) {
  return parts.map((part, index) => {
    if (part.kind === "text") return part.text;
    const presentation = chipPresentation(part, context);
    return (
      <span
        key={index}
        className={presentation.className}
        title={presentation.title}
        data-composer-chip={part.kind}
        data-reference-kind={presentation.kind}
      >
        {displayPart(part)}
      </span>
    );
  });
}

import { Tag } from "antd";
import type { HistoryEntry } from "../../state/sessionStore";
import { DiagnosticDetails } from "./DiagnosticDetails";

export function ChatMessageStatus({
  entry,
  sessionId,
  label
}: {
  entry: HistoryEntry;
  sessionId: string | null;
  label: string;
}) {
  const failure = entry.failure;
  const hasDetails = entry.status === "failed" && Boolean(failure?.diagnosticId);
  const className = ["chat-message-status", hasDetails ? "chat-message-status-interactive" : null]
    .filter(Boolean)
    .join(" ");

  const tag = (
    <Tag
      color={entry.status === "failed" ? "red" : "gold"}
      variant="solid"
      className={className}
      title={hasDetails ? undefined : entry.interruptReason ?? undefined}
      role={hasDetails ? "button" : undefined}
      tabIndex={hasDetails ? 0 : undefined}
      aria-label={hasDetails ? "Failed — show error details" : undefined}
    >
      {label}
    </Tag>
  );

  if (!hasDetails) {
    return tag;
  }

  return (
    <DiagnosticDetails
      fields={{
        diagnosticId: failure!.diagnosticId,
        correlationId: failure!.correlationId,
        sessionId,
        responseId: entry.responseId,
        category: failure!.category,
        code: failure!.code,
        failureReason: failure!.failureReason,
        providerResponseChannel: failure!.providerResponseChannel
      }}
      trigger={tag}
    />
  );
}

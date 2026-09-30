import { Button, Flex, Tag, Typography, theme } from "antd";
import type { HistoryBlock, HistoryEntry, MemoryReceiptView } from "../../state/sessionStore";
import { HistoryAttachmentView } from "./AttachmentPreview";
import { formatChatTime, statusLabel } from "./chatTime";
import { DiagnosticDetails } from "./DiagnosticDetails";
import { MarkdownMessage } from "./MarkdownMessage";
import { shouldShowSpeechText, SpokenText } from "./SpokenText";

export function ChatMessage({
  entry,
  agentName,
  sessionId,
  turnAnchor = false
}: {
  entry: HistoryEntry;
  agentName: string;
  sessionId: string | null;
  turnAnchor?: boolean;
}) {
  const { token } = theme.useToken();
  const isUser = entry.role === "user";
  const speaker = isUser ? "You" : agentName || "Agent";
  const status = statusLabel(entry.status, entry.finishReason, entry.interruptReason);
  const timeLabel = formatChatTime(entry.createdAt);
  const hasFiles = Boolean(entry.attachments?.length && sessionId);
  const hasBlocks = Boolean(entry.blocks?.length);
  const showSpeech = !isUser && shouldShowSpeechText(entry.text, entry.speechText);
  const hasBody = Boolean(entry.text) || hasBlocks || hasFiles || showSpeech;
  const blocks = hasBlocks && entry.blocks ? (
    <div className="entry-blocks">
      {entry.blocks.map((block) => (
        <RichBlock key={block.blockId} sessionId={sessionId} block={block} />
      ))}
    </div>
  ) : null;
  const files = hasFiles && entry.attachments && sessionId ? (
    <div className="entry-files">
      {entry.attachments.map((file) => (
        <HistoryAttachmentView key={file.attachmentId} sessionId={sessionId} file={file} />
      ))}
    </div>
  ) : null;

  return (
    <li
      data-role={entry.role}
      data-turn-anchor={turnAnchor ? "true" : undefined}
      className={isUser ? "chat-message chat-message-user" : "chat-message chat-message-assistant"}
    >
      <Flex align="baseline" gap={8} className="chat-message-meta">
        {isUser ? null : (
          <Typography.Text type="secondary" className="chat-message-speaker">
            {speaker}
          </Typography.Text>
        )}
        {timeLabel ? (
          <Typography.Text type="secondary" className="chat-message-time">
            <time dateTime={entry.createdAt}>{timeLabel}</time>
          </Typography.Text>
        ) : null}
      </Flex>
      {hasBody ? (
        <div className={isUser ? "user-bubble" : "assistant-body"}>
          {isUser ? (
            <>
              {entry.text ? <Typography.Paragraph className="user-bubble-text">{entry.text}</Typography.Paragraph> : null}
              {blocks}
              {files}
            </>
          ) : (
            <>
              {showSpeech && entry.speechText ? (
                <SpokenText speechText={entry.speechText} deliveryMode={entry.deliveryMode} />
              ) : null}
              {entry.text ? <MarkdownMessage source={entry.text} /> : null}
              {blocks}
              {files}
            </>
          )}
        </div>
      ) : null}
      {entry.memoryReceipts?.length ? (
        <Flex wrap gap={token.paddingXS} className="chat-message-receipts">
          {entry.memoryReceipts.map((receipt, index) => (
            <MemoryReceiptLine key={`${receipt.operation}-${receipt.subject}-${index}`} receipt={receipt} />
          ))}
        </Flex>
      ) : null}
      {status ? (
        <Flex align="center" gap={token.paddingXS} className="chat-message-status-row">
          {entry.status === "failed" && entry.failure?.diagnosticId ? (
            <DiagnosticDetails
              fields={{
                diagnosticId: entry.failure.diagnosticId,
                correlationId: entry.failure.correlationId,
                sessionId,
                responseId: entry.responseId,
                category: entry.failure.category,
                code: entry.failure.code
              }}
              trigger={
                <Button
                  danger
                  type="primary"
                  size="small"
                  className="chat-message-status"
                  aria-label="Failed — show error details"
                >
                  {status}
                </Button>
              }
            />
          ) : (
            <Tag
              color={entry.status === "failed" ? "red" : "gold"}
              variant="solid"
              className="chat-message-status"
              title={entry.interruptReason ?? undefined}
            >
              {status}
            </Tag>
          )}
        </Flex>
      ) : null}
    </li>
  );
}

function MemoryReceiptLine({ receipt }: { receipt: MemoryReceiptView }) {
  const explicit = receipt.presentation === "explicit";
  return (
    <Typography.Text type={explicit ? "danger" : "secondary"} className="chat-message-receipt">
      {explicit ? receipt.label : `✓ ${receipt.label}`}
    </Typography.Text>
  );
}

function RichBlock({ sessionId, block }: { sessionId: string | null; block: HistoryBlock }) {
  if (block.kind === "unknown") {
    return (
      <div className="entry-block" data-kind="unknown">
        {block.fallbackText || "[Unsupported content]"}
      </div>
    );
  }

  if (block.kind === "markdown") {
    return (
      <div className="entry-block" data-kind="markdown">
        <MarkdownMessage source={block.text || block.fallbackText} />
      </div>
    );
  }

  if (block.kind === "attachment" && sessionId && block.attachmentId) {
    return (
      <div className="entry-block" data-kind="attachment">
        <HistoryAttachmentView
          sessionId={sessionId}
          file={{
            attachmentId: block.attachmentId,
            displayName: block.text || block.fallbackText || "Attachment",
            contentType: ""
          }}
        />
      </div>
    );
  }

  if (block.kind === "artifact" && block.artifactId) {
    const label = block.text || block.fallbackText || block.artifactId;
    return (
      <div className="entry-block" data-kind="artifact">
        <Button type="text" className="file-chip" aria-label={`Artifact ${label}`}>
          Artifact · {label}
        </Button>
      </div>
    );
  }

  return (
    <div className="entry-block" data-kind="unknown">
      {block.fallbackText || "[Unsupported content]"}
    </div>
  );
}

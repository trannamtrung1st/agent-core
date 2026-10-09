import { ConversationDestination } from "./AutomationDestination";
import { Flex, Typography, theme } from "antd";
import type { HistoryBlock, HistoryEntry, MemoryReceiptView } from "../../state/sessionStore";
import { ArtifactView } from "./ArtifactView";
import { HistoryAttachmentView } from "./AttachmentPreview";
import { formatChatTime, statusLabel } from "./chatTime";
import { ChatMessageStatus } from "./ChatMessageStatus";
import { MarkdownMessage } from "./MarkdownMessage";
import { shouldShowSpeechText, SpokenText } from "./SpokenText";

export function ChatMessage({
  entry,
  agentName,
  sessionId,
  turnAnchor = false,
  backgroundSource = null
}: {
  entry: HistoryEntry;
  agentName: string;
  sessionId: string | null;
  turnAnchor?: boolean;
  backgroundSource?: string | null;
}) {
  const { token } = theme.useToken();
  const isUser = entry.role === "user";
  const isApplication = entry.role === "applicationMessage";
  const speaker = isUser ? "You" : agentName || "Agent";
  const status = isApplication
    ? null
    : entry.status === "failed" && entry.effectReceipts?.length
      ? "Reply failed"
      : statusLabel(entry.status, entry.finishReason, entry.interruptReason);
  const timeLabel = formatChatTime(entry.createdAt);
  const hasFiles = Boolean(entry.attachments?.length && sessionId);
  const hasBlocks = Boolean(entry.blocks?.length);
  const showSpeech = !isUser && !isApplication && shouldShowSpeechText(entry.text, entry.speechText);
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
      aria-live={isApplication ? "polite" : undefined}
      className={
        isUser
          ? "chat-message chat-message-user"
          : isApplication
            ? "chat-message chat-message-application"
            : "chat-message chat-message-assistant"
      }
    >
      <Flex align="baseline" gap={token.paddingXS} wrap className="chat-message-meta">
        {isUser ? null : (
          <Typography.Text type="secondary" className="chat-message-speaker">
            {speaker}
          </Typography.Text>
        )}
        {isApplication ? (
          <Typography.Text className="application-status">Still working</Typography.Text>
        ) : null}
        {timeLabel ? (
          <Typography.Text type="secondary" className="chat-message-time">
            <time dateTime={entry.createdAt}>{timeLabel}</time>
          </Typography.Text>
        ) : null}
        {backgroundSource ? (
          <Typography.Text type="secondary" className="chat-message-time chat-message-completion">
            <span aria-hidden="true" className="chat-message-completion-separator">·</span>Background work completed{" "}
            <ConversationDestination sessionId={backgroundSource} />
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
          {groupMemoryReceipts(entry.memoryReceipts).map(({ receipt, count }, index) => (
            <MemoryReceiptLine key={`${receipt.operation}-${receipt.subject}-${index}`} receipt={receipt} count={count} />
          ))}
        </Flex>
      ) : null}
      {entry.effectReceipts?.length ? (
        <Flex wrap gap={token.paddingXS} className="chat-message-receipts" aria-label="Recorded actions">
          {entry.effectReceipts.map((receipt) => (
            <Typography.Text key={`${receipt.tool}-${receipt.status}`} type="secondary" className="chat-message-receipt">
              {`✓ ${receipt.label}`}
            </Typography.Text>
          ))}
        </Flex>
      ) : null}
      {status ? (
        <Flex align="center" gap={token.paddingXS} className="chat-message-status-row">
          <ChatMessageStatus entry={entry} sessionId={sessionId} label={status} />
        </Flex>
      ) : null}
    </li>
  );
}

function groupMemoryReceipts(receipts: MemoryReceiptView[]) {
  const grouped = new Map<string, { receipt: MemoryReceiptView; subjects: Set<string> }>();
  const rows: Array<{ receipt: MemoryReceiptView; subjects?: Set<string> }> = [];
  for (const receipt of receipts) {
    if (receipt.presentation !== "indicator") {
      rows.push({ receipt });
      continue;
    }
    const key = `${receipt.operation}\u0000${receipt.label}`;
    const existing = grouped.get(key);
    if (existing) {
      existing.subjects.add(receipt.subject.trim().toLowerCase());
    } else {
      const row = { receipt, subjects: new Set([receipt.subject.trim().toLowerCase()]) };
      grouped.set(key, row);
      rows.push(row);
    }
  }
  return rows.map(({ receipt, subjects }) => ({ receipt, count: subjects?.size ?? 1 }));
}

function MemoryReceiptLine({ receipt, count }: { receipt: MemoryReceiptView; count: number }) {
  const explicit = receipt.presentation === "explicit";
  return (
    <Typography.Text type={explicit ? "danger" : "secondary"} className="chat-message-receipt">
      {explicit ? receipt.label : `✓ ${receipt.label}${count > 1 ? ` (${count})` : ""}`}
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
    return (
      <div className="entry-block" data-kind="artifact">
        <ArtifactView key={`${sessionId}:${block.artifactId}`} sessionId={sessionId} artifactId={block.artifactId} />
      </div>
    );
  }

  return (
    <div className="entry-block" data-kind="unknown">
      {block.fallbackText || "[Unsupported content]"}
    </div>
  );
}

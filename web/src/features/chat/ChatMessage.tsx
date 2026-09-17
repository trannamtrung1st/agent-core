import { Tag, Typography } from "antd";
import type { HistoryBlock, HistoryEntry } from "../../state/sessionStore";
import { HistoryAttachmentView } from "./AttachmentPreview";
import { MarkdownMessage } from "./MarkdownMessage";

export function ChatMessage({
  entry,
  agentName,
  sessionId
}: {
  entry: HistoryEntry;
  agentName: string;
  sessionId: string | null;
  connection?: string;
}) {
  const isUser = entry.role === "user";
  const speaker = isUser ? "You" : agentName || "Agent";
  const status = entry.status === "interrupted" || entry.status === "failed" ? entry.status : null;

  return (
    <li data-role={entry.role} className={isUser ? "chat-message chat-message-user" : "chat-message chat-message-assistant"}>
      {isUser ? null : (
        <Typography.Text type="secondary" className="chat-message-speaker">
          {speaker}
        </Typography.Text>
      )}
      <div className={isUser ? "user-bubble" : "assistant-body"}>
        {isUser ? (
          entry.text ? <Typography.Paragraph className="user-bubble-text">{entry.text}</Typography.Paragraph> : null
        ) : entry.text ? (
          <MarkdownMessage source={entry.text} />
        ) : null}
        {status ? (
          <Tag color={status === "failed" ? "error" : "warning"} className="chat-message-status">
            {status}
          </Tag>
        ) : null}
        {entry.blocks && entry.blocks.length > 0 ? (
          <div className="entry-blocks">
            {entry.blocks.map((block) => (
              <RichBlock key={block.blockId} sessionId={sessionId} block={block} />
            ))}
          </div>
        ) : null}
        {entry.attachments && entry.attachments.length > 0 && sessionId ? (
          <div className="entry-files">
            {entry.attachments.map((file) => (
              <HistoryAttachmentView key={file.attachmentId} sessionId={sessionId} file={file} />
            ))}
          </div>
        ) : null}
      </div>
    </li>
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
        Artifact · {block.artifactId}
      </div>
    );
  }

  return (
    <div className="entry-block" data-kind="unknown">
      {block.fallbackText || "[Unsupported content]"}
    </div>
  );
}

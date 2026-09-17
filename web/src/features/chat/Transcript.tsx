import { useEffect, useState } from "react";
import { Empty, Spin, Tag, Typography } from "antd";
import { fetchAttachmentBlob } from "../../services/attachments";
import type { HistoryAttachment, HistoryBlock, HistoryEntry } from "../../state/sessionStore";
import { parseSanitizedMarkdown } from "./sanitizedMarkdown";

export function Transcript({
  agentName,
  sessionId,
  entries,
  connection = "ready"
}: {
  agentName: string;
  sessionId: string | null;
  entries: HistoryEntry[];
  connection?: string;
}) {
  const entryCount = entries.length;
  const loading = connection === "connecting" || connection === "reconnecting";

  return (
    <section className="transcript-window" aria-label="Transcript">
      <Typography.Text strong>
        Transcript · {entryCount} {entryCount === 1 ? "entry" : "entries"}
      </Typography.Text>
      <Spin spinning={loading} description="Loading conversation">
        <ol className="transcript" aria-live="polite">
          {entries.length === 0 ? (
            <li className="entry entry-empty">
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Send a message or start voice." />
            </li>
          ) : (
            entries.map((entry) => {
              const isUser = entry.role === "user";
              const speaker = isUser ? "You" : agentName || "Agent";
              return (
                <li key={entry.entryId} data-role={entry.role} className="entry">
                  <Typography.Paragraph style={{ marginBottom: 0 }}>
                    <Typography.Text strong>{speaker}</Typography.Text>
                    <Typography.Text type="secondary"> — </Typography.Text>
                    <Typography.Text>{entry.text}</Typography.Text>
                    {entry.status === "interrupted" || entry.status === "failed" ? (
                      <Tag color={entry.status === "failed" ? "error" : "warning"} style={{ marginInlineStart: 8 }}>
                        {entry.status}
                      </Tag>
                    ) : null}
                    {entry.blocks && entry.blocks.length > 0 ? (
                      <span className="entry-blocks">
                        {entry.blocks.map((block) => (
                          <RichBlock key={block.blockId} sessionId={sessionId} block={block} />
                        ))}
                      </span>
                    ) : null}
                    {entry.attachments && entry.attachments.length > 0 && sessionId ? (
                      <span className="entry-files">
                        {entry.attachments.map((file) => (
                          <HistoryFile key={file.attachmentId} sessionId={sessionId} file={file} />
                        ))}
                      </span>
                    ) : null}
                  </Typography.Paragraph>
                </li>
              );
            })
          )}
        </ol>
      </Spin>
    </section>
  );
}

function RichBlock({ sessionId, block }: { sessionId: string | null; block: HistoryBlock }) {
  if (block.kind === "unknown") {
    return (
      <span className="entry-block" data-kind="unknown">
        {block.fallbackText || "[Unsupported content]"}
      </span>
    );
  }

  if (block.kind === "markdown") {
    return (
      <span className="entry-block" data-kind="markdown">
        <SanitizedMarkdown source={block.text || block.fallbackText} />
      </span>
    );
  }

  if (block.kind === "attachment" && sessionId && block.attachmentId) {
    return (
      <span className="entry-block" data-kind="attachment">
        <HistoryFile
          sessionId={sessionId}
          file={{
            attachmentId: block.attachmentId,
            displayName: block.text || block.fallbackText || "Attachment",
            contentType: ""
          }}
        />
      </span>
    );
  }

  if (block.kind === "artifact" && block.artifactId) {
    return (
      <span className="entry-block" data-kind="artifact">
        Artifact · {block.artifactId}
      </span>
    );
  }

  return (
    <span className="entry-block" data-kind="unknown">
      {block.fallbackText || "[Unsupported content]"}
    </span>
  );
}

function SanitizedMarkdown({ source }: { source: string }) {
  return (
    <>
      {parseSanitizedMarkdown(source).map((node, index) => {
        if (node.type === "strong") {
          return <strong key={index}>{node.value}</strong>;
        }

        if (node.type === "em") {
          return <em key={index}>{node.value}</em>;
        }

        if (node.type === "code") {
          return (
            <code key={index} className="entry-md-code">
              {node.value}
            </code>
          );
        }

        if (node.type === "link") {
          return (
            <a key={index} className="entry-file" href={node.href} rel="noreferrer noopener" target="_blank">
              {node.value}
            </a>
          );
        }

        return <span key={index}>{node.value}</span>;
      })}
    </>
  );
}

function HistoryFile({ sessionId, file }: { sessionId: string; file: HistoryAttachment }) {
  const [href, setHref] = useState<string | null>(null);

  useEffect(() => {
    let objectUrl: string | null = null;
    let cancelled = false;
    void fetchAttachmentBlob(sessionId, file.attachmentId)
      .then((blob) => {
        if (cancelled) {
          return;
        }
        objectUrl = URL.createObjectURL(blob);
        setHref(objectUrl);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl);
      }
    };
  }, [sessionId, file.attachmentId]);

  if (!href) {
    return <Typography.Text>{file.displayName}</Typography.Text>;
  }

  if (file.contentType.startsWith("image/")) {
    return (
      <a className="entry-file" href={href} download={file.displayName}>
        <img className="entry-preview" src={href} alt={file.displayName} />
      </a>
    );
  }

  return (
    <a className="entry-file" href={href} download={file.displayName}>
      {file.displayName}
    </a>
  );
}

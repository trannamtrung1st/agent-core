import { useEffect, useState } from "react";
import { fetchAttachmentBlob } from "../../services/attachments";
import type { HistoryAttachment, HistoryBlock, HistoryEntry } from "../../state/sessionStore";
import { parseSanitizedMarkdown } from "./sanitizedMarkdown";

export function Transcript({
  agentName,
  sessionId,
  entries
}: {
  agentName: string;
  sessionId: string | null;
  entries: HistoryEntry[];
}) {
  const entryCount = entries.length;

  return (
    <section className="transcript-window" aria-label="Transcript">
      <div className="transcript-bar">
        <p className="transcript-title">
          <span className="marker marker-square" aria-hidden="true" />
          Transcript · {entryCount} {entryCount === 1 ? "entry" : "entries"}
        </p>
        <p className="live">
          Agent Core · Live
          <span className="marker marker-square" aria-hidden="true" />
        </p>
      </div>
      <div className="transcript-well">
        <div className="transcript-grid" aria-hidden="true" />
        <ol className="transcript" aria-live="polite">
          {entries.length === 0 ? (
            <li className="entry entry-empty">Send a message or start voice.</li>
          ) : (
            entries.map((entry) => {
              const isUser = entry.role === "user";
              const speaker = isUser ? "You" : agentName || "Agent";
              return (
                <li key={entry.entryId} data-role={entry.role} className="entry">
                  <span className={`marker ${isUser ? "marker-plus" : "marker-diamond"}`} aria-hidden="true" />
                  <p className="entry-copy">
                    <strong>{speaker}</strong>
                    <span className="emdash" aria-hidden="true" />
                    {entry.text}
                    {entry.status === "interrupted" || entry.status === "failed" ? <em>{entry.status}</em> : null}
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
                  </p>
                </li>
              );
            })
          )}
        </ol>
      </div>
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
    return <span className="entry-file">{file.displayName}</span>;
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

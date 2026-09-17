import { useEffect, useState } from "react";
import { Button, Flex, Progress, Typography } from "antd";
import { FileOutlined } from "@ant-design/icons";
import { fetchAttachmentBlob, pendingPreviewUrl, type PendingAttachment } from "../../services/attachments";
import type { HistoryAttachment } from "../../state/sessionStore";

export function HistoryAttachmentView({ sessionId, file }: { sessionId: string; file: HistoryAttachment }) {
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
    return (
      <span className="file-chip">
        <FileOutlined aria-hidden />
        <Typography.Text>{file.displayName}</Typography.Text>
      </span>
    );
  }

  if (file.contentType.startsWith("image/")) {
    return (
      <a className="entry-file" href={href} target="_blank" rel="noreferrer noopener">
        <img className="entry-preview" src={href} alt={file.displayName} />
      </a>
    );
  }

  return (
    <a className="file-chip" href={href} download={file.displayName} aria-label={`Download ${file.displayName}`}>
      <FileOutlined aria-hidden />
      <span>
        <Typography.Text>{file.displayName}</Typography.Text>
        <Typography.Text type="secondary">{fileKind(file.contentType)}</Typography.Text>
      </span>
    </a>
  );
}

export function PendingAttachmentView({
  item,
  onRetry,
  onRemove
}: {
  item: PendingAttachment;
  onRetry: () => void;
  onRemove: () => void;
}) {
  const preview = item.contentType.startsWith("image/") ? pendingPreviewUrl(item.localId) : null;

  return (
    <li className="attach-chip">
      <Flex align="center" gap={8} wrap="wrap">
        {preview ? <img className="attach-preview" src={preview} alt={item.displayName} /> : <FileOutlined aria-hidden />}
        <Typography.Text strong>{item.displayName}</Typography.Text>
        {item.status === "uploading" ? <Progress percent={item.progress} size="small" style={{ width: 120 }} /> : null}
        {item.status === "error" && item.error ? (
          <Typography.Text type="danger">{item.error}</Typography.Text>
        ) : null}
        {item.status === "error" ? (
          <Button size="small" aria-label={`Retry ${item.displayName}`} onClick={onRetry}>
            Retry
          </Button>
        ) : null}
        <Button size="small" aria-label={`Remove ${item.displayName}`} onClick={onRemove}>
          Remove
        </Button>
      </Flex>
    </li>
  );
}

function fileKind(contentType: string): string {
  if (contentType.includes("pdf")) {
    return "PDF";
  }
  if (contentType.startsWith("image/")) {
    return "Image";
  }
  if (contentType.startsWith("text/")) {
    return "Text";
  }
  return contentType || "File";
}

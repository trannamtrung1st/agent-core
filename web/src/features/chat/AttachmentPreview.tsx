import { useEffect, useRef, useState } from "react";
import { Button, Flex, Progress, Typography } from "antd";
import { artifactKind } from "../../services/artifacts";
import { FileOutlined } from "@ant-design/icons";
import { pendingPreviewUrl, type PendingAttachment } from "../../services/attachments";
import type { HistoryAttachment } from "../../state/sessionStore";

import { downloadFile, readFile, resolveFile, type FileRef } from "../filePreview/fileSource";
import { previewFormat, previewLimits, validateImageDimensions, validateSignature } from "../filePreview/previewPolicy";
import { FilePreviewButton } from "../filePreview/FilePreview";

export function HistoryAttachmentView({ sessionId, file }: { sessionId: string; file: HistoryAttachment }) {
  return <HistoryFile key={`${sessionId}:${file.attachmentId}`} sessionId={sessionId} file={file} />;
}
function HistoryFile({ sessionId, file }: { sessionId: string; file: HistoryAttachment }) {
  const [href, setHref] = useState<string | null>(null); const [failed, setFailed] = useState(false); const [busy, setBusy] = useState(false);
  const downloads = useRef<AbortController | null>(null);
  const ref: FileRef = { kind: 'attachment', sessionId, attachmentId: file.attachmentId };
  useEffect(() => {
    const controller = new AbortController(); let url: string | null = null;
    if (file.contentType.startsWith('image/')) void (async () => {
      const metadata = await resolveFile(ref, controller.signal);
      if (previewFormat(metadata) !== 'image') return;
      const blob = await readFile(ref, metadata, previewLimits.image, controller.signal);
      await validateSignature(blob, metadata.contentType); await validateImageDimensions(blob, metadata.contentType); controller.signal.throwIfAborted();
      url = URL.createObjectURL(blob); setHref(url);
    })().catch(() => undefined);
    return () => { controller.abort(); downloads.current?.abort(); if (url) URL.revokeObjectURL(url); };
  }, [sessionId, file.attachmentId, file.contentType]);
  async function save() {
    if (downloads.current) return;
    const controller = new AbortController(); downloads.current = controller; setBusy(true); setFailed(false);
    try { await downloadFile(ref, controller.signal); }
    catch { if (!controller.signal.aborted) setFailed(true); }
    finally { if (!controller.signal.aborted) { setBusy(false); downloads.current = null; } }
  }
  return <Flex className="history-file-actions" wrap align="center" gap="small">
    {href ? <a className="entry-file" href={href} target="_blank" rel="noreferrer noopener" aria-label={`Open ${file.displayName}`}><img className="entry-preview" loading="lazy" decoding="async" src={href} alt={file.displayName} /></a>
      : <a className="file-chip" href="#" download={file.displayName} aria-label={`Download ${file.displayName}`} aria-busy={busy} onClick={event=>{event.preventDefault();void save();}}><FileOutlined aria-hidden /><span><Typography.Text>{file.displayName}</Typography.Text><Typography.Text type="secondary">{artifactKind(file.contentType)}</Typography.Text></span></a>}
    <FilePreviewButton file={ref} label={`Preview ${file.displayName}`} />
    <Button type="text" loading={busy} aria-label={`Download ${file.displayName}`} onClick={() => void save()}>Download</Button>
    {failed && <Typography.Text type="danger" role="status">Download failed. Try again.</Typography.Text>}
  </Flex>;
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

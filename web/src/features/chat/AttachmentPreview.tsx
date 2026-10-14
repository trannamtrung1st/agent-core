import { useEffect, useRef, useState } from "react";
import { Button, Flex, Progress, Tooltip, Typography, theme } from "antd";
import { artifactKind } from "../../services/artifacts";
import { CloseOutlined, FileOutlined, ReloadOutlined } from "@ant-design/icons";
import { type PendingAttachment } from "../../services/attachments";
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
  const { token } = theme.useToken();

  return (
    <li className="attach-chip" aria-busy={item.status === "uploading"}>
      <Flex align="center" gap={token.paddingXS} wrap={false}>
        <FileOutlined aria-hidden style={{ fontSize: token.fontSizeLG }} />
        <Tooltip title={item.displayName} trigger={["hover", "focus"]}>
          <Typography.Text className="attach-filename single-line-ellipsis" tabIndex={0}>
            {item.displayName}
          </Typography.Text>
        </Tooltip>
        {item.status === "error" && <Tooltip title={`Retry ${item.displayName}`}>
          <Button type="text" className="composer-icon" aria-label={`Retry ${item.displayName}`} icon={<ReloadOutlined />} onClick={onRetry} />
        </Tooltip>}
        <Tooltip title={`Remove ${item.displayName}`}>
          <Button type="text" className="composer-icon" aria-label={`Remove ${item.displayName}`} icon={<CloseOutlined />} onClick={onRemove} />
        </Tooltip>
      </Flex>
      {item.status === "uploading" && <Progress aria-label={`Uploading ${item.displayName}`} percent={item.progress} size="small" />}
      {item.status === "error" && <Typography.Text type="danger" role="status">{item.error || "Upload failed. Retry or remove this file."}</Typography.Text>}
    </li>
  );
}

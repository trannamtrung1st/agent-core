import { useEffect, useRef, useState } from "react";
import { Button, Flex, Progress, Tooltip, Typography, theme } from "antd";
import { artifactKind } from "../../services/artifacts";
import { CloseOutlined, FileOutlined, ReloadOutlined } from "@ant-design/icons";
import { type PendingAttachment } from "../../services/attachments";
import type { HistoryAttachment } from "../../state/sessionStore";

import { downloadFile, readFile, resolveFile, type FileRef } from "../filePreview/fileSource";
import { previewFormat, formatLimit, validateImageDimensions, validateSignature } from "../filePreview/previewPolicy";
import { loadPreviewOptions } from "../filePreview/previewOptions";
import { AttachmentMaterializeAction } from "./AttachmentMaterializeAction";
import { ChatFileCard } from "./ChatFileCard";

export function HistoryAttachmentView({ sessionId, tenantId, file }: { sessionId: string; tenantId?: string; file: HistoryAttachment }) {
  if (!tenantId) return <ChatFileCard metadataFailed onDownload={() => {}} />;
  return <HistoryFile key={`${tenantId ?? ""}:${sessionId}:${file.attachmentId}`} sessionId={sessionId} tenantId={tenantId} file={file} />;
}
function HistoryFile({ sessionId, tenantId, file }: { sessionId: string; tenantId: string; file: HistoryAttachment }) {
  const [href, setHref] = useState<string | null>(null); const [failed, setFailed] = useState(false); const [busy, setBusy] = useState(false);
  const downloads = useRef<AbortController | null>(null);
  const ref: FileRef = { kind: 'attachment', sessionId, attachmentId: file.attachmentId, tenantId };
  useEffect(() => {
    const controller = new AbortController(); let url: string | null = null;
    if (file.contentType.startsWith('image/')) void (async () => {
      const metadata = await resolveFile(ref, controller.signal);
      if (previewFormat(metadata) !== 'image') return;
      const policy = await loadPreviewOptions(tenantId, controller.signal);
      const blob = await readFile(ref, metadata, formatLimit('image', policy), controller.signal);
      await validateSignature(blob, metadata.contentType); await validateImageDimensions(blob, metadata.contentType, policy); controller.signal.throwIfAborted();
      url = URL.createObjectURL(blob); setHref(url);
    })().catch(() => undefined);
    return () => { controller.abort(); downloads.current?.abort(); if (url) URL.revokeObjectURL(url); };
  }, [tenantId, sessionId, file.attachmentId, file.contentType]);
  async function save() {
    if (downloads.current) return;
    const controller = new AbortController(); downloads.current = controller; setBusy(true); setFailed(false);
    try { await downloadFile(ref, controller.signal); }
    catch { if (!controller.signal.aborted) setFailed(true); }
    finally { if (!controller.signal.aborted) { setBusy(false); downloads.current = null; } }
  }
  return <div className="history-file-actions">
    {href && <a className="entry-file" href={href} target="_blank" rel="noreferrer noopener" aria-label={`Open ${file.displayName}`}><img className="entry-preview" loading="lazy" decoding="async" src={href} alt={file.displayName} /></a>}
    <ChatFileCard displayName={file.displayName} description={artifactKind(file.contentType)} fileRef={ref}
      downloading={busy} downloadFailed={failed} onDownload={() => void save()}
      sourceAction={tenantId && <AttachmentMaterializeAction key={`${tenantId}:${sessionId}:${file.attachmentId}`} tenantId={tenantId} sessionId={sessionId} attachmentId={file.attachmentId} displayName={file.displayName} />} />
  </div>;
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
        {item.originalText !== undefined && item.status === 'ready' ? <Button type="text" size="small" onClick={onRemove}>Restore text</Button> : null}
        <Tooltip title={`Remove ${item.displayName}`}>
          <Button type="text" className="composer-icon" aria-label={`Remove ${item.displayName}`} icon={<CloseOutlined />} onClick={onRemove} />
        </Tooltip>
      </Flex>
      {item.status === "uploading" && <Progress aria-label={`Uploading ${item.displayName}`} percent={item.progress} size="small" />}
      {item.status === "error" && <Typography.Text type="danger" role="status">{item.error || "Upload failed. Retry or remove this file."}</Typography.Text>}
    </li>
  );
}

import { useRef, type ClipboardEvent, type DragEvent } from "react";
import { Alert, Button, Flex, Input, Progress, Typography } from "antd";
import {
  queueComposerFiles,
  removeComposerFile,
  retryComposerFile
} from "../../services/realtime";
import { pendingPreviewUrl, type PendingAttachment } from "../../services/attachments";

export function Composer({
  draft,
  canSend,
  ready,
  error,
  pendingAttachments,
  voiceAvailable,
  pendingVoice,
  voiceLive,
  muted,
  canRetry,
  onDraftChange,
  onSend,
  onVoice,
  onCancelVoice,
  onMute,
  onRetry,
  onEnd
}: {
  draft: string;
  canSend: boolean;
  ready: boolean;
  error: string | null;
  pendingAttachments: PendingAttachment[];
  voiceAvailable: boolean;
  pendingVoice: boolean;
  voiceLive: boolean;
  muted: boolean;
  canRetry: boolean;
  onDraftChange: (value: string) => void;
  onSend: () => void;
  onVoice: () => void;
  onCancelVoice: () => void;
  onMute: (muted: boolean) => void;
  onRetry: () => void;
  onEnd: () => void;
}) {
  const fileInput = useRef<HTMLInputElement>(null);

  function takeFiles(list: FileList | File[] | null) {
    if (!list || !ready) {
      return;
    }

    const files = Array.from(list);
    if (files.length === 0) {
      return;
    }

    void queueComposerFiles(files);
  }

  function onDrop(event: DragEvent<HTMLFormElement>) {
    event.preventDefault();
    takeFiles(event.dataTransfer.files);
  }

  function onPaste(event: ClipboardEvent<HTMLTextAreaElement>) {
    const files = event.clipboardData.files;
    if (files.length > 0) {
      event.preventDefault();
      takeFiles(files);
    }
  }

  return (
    <Flex vertical gap={8} className="dock">
      {error ? <Alert type="error" showIcon title={error} /> : null}

      <form
        className="composer"
        onSubmit={(event) => {
          event.preventDefault();
          onSend();
        }}
        onDragOver={(event) => event.preventDefault()}
        onDrop={onDrop}
      >
        <Flex vertical gap={8}>
          <label>
            <Flex vertical gap={8}>
              <Typography.Text>Message</Typography.Text>
              <Input.TextArea
                className="message-field"
                value={draft}
                onChange={(event) => onDraftChange(event.target.value)}
                onPaste={onPaste}
                onKeyDown={(event) => {
                  if (event.key === "Enter" && !event.shiftKey) {
                    event.preventDefault();
                    onSend();
                  }
                }}
                disabled={!ready}
                rows={2}
                placeholder="Type your message here..."
                aria-label="Message"
              />
            </Flex>
          </label>
          {pendingAttachments.length > 0 ? (
            <ul className="attach-list" aria-label="Pending attachments">
              {pendingAttachments.map((item) => (
                <li key={item.localId} className="attach-chip">
                  <Flex align="center" gap={8} wrap="wrap">
                    {item.contentType.startsWith("image/") && pendingPreviewUrl(item.localId) ? (
                      <img className="attach-preview" src={pendingPreviewUrl(item.localId)!} alt="" />
                    ) : null}
                    <Typography.Text strong>{item.displayName}</Typography.Text>
                    {item.status === "uploading" ? <Progress percent={item.progress} size="small" style={{ width: 120 }} /> : null}
                    {item.status === "error" && item.error ? (
                      <Typography.Text type="danger">{item.error}</Typography.Text>
                    ) : null}
                    {item.status === "error" ? (
                      <Button
                        size="small"
                        aria-label={`Retry ${item.displayName}`}
                        onClick={() => void retryComposerFile(item.localId)}
                      >
                        Retry
                      </Button>
                    ) : null}
                    <Button
                      size="small"
                      aria-label={`Remove ${item.displayName}`}
                      onClick={() => void removeComposerFile(item.localId)}
                    >
                      Remove
                    </Button>
                  </Flex>
                </li>
              ))}
            </ul>
          ) : null}
          <input
            ref={fileInput}
            className="attach-input"
            type="file"
            multiple
            aria-hidden="true"
            tabIndex={-1}
            onChange={(event) => {
              takeFiles(event.target.files);
              event.target.value = "";
            }}
          />
          <Flex justify="space-between" align="center" gap={8} wrap="wrap">
            <Flex gap={8} wrap="wrap">
              <Button aria-label="Attach" disabled={!ready} onClick={() => fileInput.current?.click()}>
                Attach
              </Button>
              <Button type="primary" htmlType="submit" aria-label="Send" disabled={!canSend}>
                Send
              </Button>
              {voiceAvailable ? (
                pendingVoice ? (
                  <Button aria-label="Cancel voice" onClick={onCancelVoice}>
                    Cancel
                  </Button>
                ) : voiceLive ? (
                  <Button aria-label={muted ? "Unmute" : "Mute"} onClick={() => onMute(!muted)}>
                    {muted ? "Unmute" : "Mute"}
                  </Button>
                ) : (
                  <Button aria-label="Voice" onClick={onVoice}>
                    Voice
                  </Button>
                )
              ) : null}
              {canRetry ? (
                <Button aria-label="Retry" onClick={onRetry}>
                  Retry
                </Button>
              ) : null}
            </Flex>
            <Button danger aria-label="End" onClick={onEnd}>
              End
            </Button>
          </Flex>
        </Flex>
      </form>
    </Flex>
  );
}

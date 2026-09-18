import { useRef, useState, type ClipboardEvent, type DragEvent } from "react";
import { Button, Flex, Input, Tooltip } from "antd";
import {
  AudioOutlined,
  AudioMutedOutlined,
  DeleteOutlined,
  DownOutlined,
  PaperClipOutlined,
  RightOutlined,
  SendOutlined,
  StopOutlined,
  UnorderedListOutlined
} from "@ant-design/icons";
import type { InputRef } from "antd";
import {
  composerSteerEnabled,
  queueComposerFiles,
  removeComposerFile,
  retryComposerFile,
  removeQueuedSend,
  steerQueuedSend
} from "../../services/realtime";
import type { PendingSendItem } from "../../state/sessionStore";
import type { PendingAttachment } from "../../services/attachments";
import { PendingAttachmentView } from "./AttachmentPreview";
import { SessionFailureAlert } from "./SessionFailureAlert";
import type { SessionErrorView } from "./sessionError";

const QUEUE_COLLAPSED_VISIBLE = 2;
const QUEUE_EXPANDED_MAX = 6;

function queuePreview(item: PendingSendItem): string {
  if (item.text.trim().length > 0) {
    return item.text.trim();
  }

  if (item.attachments.length > 0) {
    return item.attachments.map((file) => file.displayName).join(", ");
  }

  return "Empty message";
}

export function Composer({
  draft,
  canSend,
  canStop,
  sendLabel,
  pendingSendQueue,
  ready,
  error,
  pendingAttachments,
  voiceAvailable,
  pendingVoice,
  voiceModeActive,
  voiceInputLive,
  voiceInputBlocked,
  muted,
  canRetry,
  placeholder,
  onDraftChange,
  onSend,
  onStop,
  onVoice,
  onCancelVoice,
  onMute,
  onRetry
}: {
  draft: string;
  canSend: boolean;
  canStop: boolean;
  sendLabel: string;
  pendingSendQueue: PendingSendItem[];
  ready: boolean;
  error: SessionErrorView | string | null;
  pendingAttachments: PendingAttachment[];
  voiceAvailable: boolean;
  pendingVoice: boolean;
  voiceModeActive: boolean;
  voiceInputLive: boolean;
  voiceInputBlocked: boolean;
  muted: boolean;
  canRetry: boolean;
  placeholder: string;
  onDraftChange: (value: string) => void;
  onSend: () => void;
  onStop: () => void;
  onVoice: () => void;
  onCancelVoice: () => void;
  onMute: (muted: boolean) => void;
  onRetry: () => void;
}) {
  const fileInput = useRef<HTMLInputElement>(null);
  const messageRef = useRef<InputRef>(null);
  const [queueExpanded, setQueueExpanded] = useState(false);

  function focusMessage() {
    messageRef.current?.focus();
  }

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

  const queueNeedsScroll = pendingSendQueue.length > QUEUE_EXPANDED_MAX;
  const visibleQueue = queueExpanded
    ? pendingSendQueue
    : pendingSendQueue.slice(0, QUEUE_COLLAPSED_VISIBLE);
  const hiddenCount = Math.max(0, pendingSendQueue.length - visibleQueue.length);

  return (
    <Flex vertical gap={8} className="dock">
      {error ? <SessionFailureAlert error={error} /> : null}

      {pendingSendQueue.length > 0 ? (
        <section className="pending-send-queue" aria-label="Queued messages">
          <Flex justify="space-between" align="center" className="pending-send-queue-header">
            {pendingSendQueue.length > QUEUE_COLLAPSED_VISIBLE ? (
              <button
                type="button"
                className="pending-send-queue-toggle"
                aria-expanded={queueExpanded}
                aria-label={queueExpanded ? "Collapse queued messages" : "Expand queued messages"}
                onClick={() => setQueueExpanded((value) => !value)}
              >
                <span>Queued · {pendingSendQueue.length}</span>
                {queueExpanded ? (
                  <DownOutlined aria-hidden="true" />
                ) : (
                  <RightOutlined aria-hidden="true" />
                )}
              </button>
            ) : (
              <span className="pending-send-queue-label">Queued · {pendingSendQueue.length}</span>
            )}
          </Flex>
          <ul
            className={`pending-send-queue-list${queueNeedsScroll && queueExpanded ? " pending-send-queue-list-scroll" : ""}`}
          >
            {visibleQueue.map((item) => {
              const position = pendingSendQueue.findIndex((queued) => queued.localId === item.localId) + 1;
              const steerEnabled = composerSteerEnabled(item.localId);
              return (
                <li key={item.localId} className="pending-send-queue-item">
                  <UnorderedListOutlined className="pending-send-queue-icon" aria-hidden="true" />
                  <div className="pending-send-queue-body">
                    <span className="pending-send-queue-text" title={queuePreview(item)}>
                      {queuePreview(item)}
                    </span>
                    {item.attachments.length > 0 ? (
                      <span className="pending-send-queue-attachments">
                        {item.attachments.map((file) => file.displayName).join(", ")}
                      </span>
                    ) : null}
                    {item.dispatching ? (
                      <span className="pending-send-queue-status">Sending…</span>
                    ) : item.error ? (
                      <span className="pending-send-queue-status pending-send-queue-status-error">{item.error}</span>
                    ) : null}
                  </div>
                  <Flex gap={4} align="center" className="pending-send-queue-actions">
                    <Tooltip title="Steer">
                      <Button
                        type="text"
                        size="small"
                        disabled={!steerEnabled}
                        aria-label={`Steer queued message ${position}`}
                        onClick={() => void steerQueuedSend(item.localId)}
                      >
                        Steer
                      </Button>
                    </Tooltip>
                    <Tooltip title="Remove">
                      <Button
                        type="text"
                        size="small"
                        disabled={item.dispatching}
                        aria-label={`Remove queued message ${position}`}
                        icon={<DeleteOutlined />}
                        onClick={() => void removeQueuedSend(item.localId)}
                      />
                    </Tooltip>
                  </Flex>
                </li>
              );
            })}
          </ul>
          {!queueExpanded && hiddenCount > 0 ? (
            <span className="pending-send-queue-more">{hiddenCount} more queued</span>
          ) : null}
        </section>
      ) : null}

      <form
        className="composer composer-shell"
        onSubmit={(event) => {
          event.preventDefault();
          onSend();
        }}
        onDragOver={(event) => event.preventDefault()}
        onDrop={onDrop}
      >
        {pendingAttachments.length > 0 ? (
          <ul className="attach-list" aria-label="Pending attachments">
            {pendingAttachments.map((item) => (
              <PendingAttachmentView
                key={item.localId}
                item={item}
                onRetry={() => void retryComposerFile(item.localId)}
                onRemove={() => void removeComposerFile(item.localId)}
              />
            ))}
          </ul>
        ) : null}
        <Input.TextArea
          ref={messageRef}
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
          autoSize={{ minRows: 1, maxRows: 8 }}
          placeholder={placeholder}
          aria-label="Message"
        />
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
        <Flex justify="space-between" align="center" gap={8} className="composer-toolbar">
          <Tooltip title="Attach">
            <Button
              type="text"
              aria-label="Attach"
              disabled={!ready}
              icon={<PaperClipOutlined />}
              onClick={() => fileInput.current?.click()}
            />
          </Tooltip>
          <Flex gap={8} align="center">
            {voiceAvailable ? (
              pendingVoice ? (
                <Button aria-label="Cancel voice" onClick={onCancelVoice}>
                  Cancel
                </Button>
              ) : voiceModeActive && voiceInputBlocked ? (
                <Tooltip title="Retry voice input">
                  <Button type="text" aria-label="Retry voice input" icon={<AudioOutlined />} onClick={onVoice} />
                </Tooltip>
              ) : voiceModeActive && muted ? (
                <Tooltip title="Unmute">
                  <Button
                    type="text"
                    aria-label="Unmute"
                    icon={<AudioMutedOutlined />}
                    onClick={() => onMute(false)}
                  />
                </Tooltip>
              ) : voiceModeActive && voiceInputLive ? (
                <Tooltip title="Mute">
                  <Button
                    type="text"
                    aria-label="Mute"
                    icon={<AudioOutlined />}
                    onClick={() => onMute(true)}
                  />
                </Tooltip>
              ) : (
                <Tooltip title="Voice">
                  <Button type="text" aria-label="Voice" icon={<AudioOutlined />} onClick={onVoice} />
                </Tooltip>
              )
            ) : null}
            {canRetry ? (
              <Button aria-label="Retry" onClick={onRetry}>
                Retry
              </Button>
            ) : null}
            {canStop ? (
              <Tooltip title="Stop">
                <Button
                  htmlType="button"
                  className="composer-stop"
                  aria-label="Stop"
                  icon={<StopOutlined />}
                  onClick={() => {
                    onStop();
                    focusMessage();
                  }}
                />
              </Tooltip>
            ) : null}
            <Tooltip title={sendLabel}>
              <Button
                type="primary"
                htmlType="submit"
                className="composer-send"
                aria-label={sendLabel}
                disabled={!canSend}
                icon={<SendOutlined />}
              />
            </Tooltip>
          </Flex>
        </Flex>
      </form>
    </Flex>
  );
}

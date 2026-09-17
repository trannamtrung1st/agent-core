import { useRef, type ClipboardEvent, type DragEvent } from "react";
import { Alert, Button, Flex, Input, Tooltip } from "antd";
import {
  AudioOutlined,
  AudioMutedOutlined,
  PaperClipOutlined,
  SendOutlined,
  StopOutlined
} from "@ant-design/icons";
import type { InputRef } from "antd";
import {
  queueComposerFiles,
  removeComposerFile,
  retryComposerFile
} from "../../services/realtime";
import type { PendingAttachment } from "../../services/attachments";
import { PendingAttachmentView } from "./AttachmentPreview";

export function Composer({
  draft,
  canSend,
  canStop,
  ready,
  error,
  pendingAttachments,
  voiceAvailable,
  pendingVoice,
  voiceLive,
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
  ready: boolean;
  error: string | null;
  pendingAttachments: PendingAttachment[];
  voiceAvailable: boolean;
  pendingVoice: boolean;
  voiceLive: boolean;
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

  return (
    <Flex vertical gap={8} className="dock">
      {error ? <Alert type="error" showIcon title={error} /> : null}

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
              ) : voiceLive ? (
                <Tooltip title={muted ? "Unmute" : "Mute"}>
                  <Button
                    type="text"
                    aria-label={muted ? "Unmute" : "Mute"}
                    icon={muted ? <AudioMutedOutlined /> : <AudioOutlined />}
                    onClick={() => onMute(!muted)}
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
            <Tooltip title="Send">
              <Button
                type="primary"
                htmlType="submit"
                className="composer-send"
                aria-label="Send"
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

import { useRef, type DragEvent, type ClipboardEvent } from "react";
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
    <div className="dock">
      {error ? (
        <p className="error" role="alert">
          {error}
        </p>
      ) : null}

      <form
        className="composer"
        onSubmit={(event) => {
          event.preventDefault();
          onSend();
        }}
        onDragOver={(event) => event.preventDefault()}
        onDrop={onDrop}
      >
        <div className="message-stack">
          <label className="message-field-wrap">
            <span className="message-label">Message</span>
            <textarea
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
            />
          </label>
          {pendingAttachments.length > 0 ? (
            <ul className="attach-list" aria-label="Pending attachments">
              {pendingAttachments.map((item) => (
                <li key={item.localId} className="attach-chip">
                  {item.contentType.startsWith("image/") && pendingPreviewUrl(item.localId) ? (
                    <img className="attach-preview" src={pendingPreviewUrl(item.localId)!} alt="" />
                  ) : (
                    <span className="marker marker-square" aria-hidden="true" />
                  )}
                  <span className="attach-copy">
                    <strong>{item.displayName}</strong>
                    {item.status === "uploading" ? <em> {item.progress}%</em> : null}
                    {item.status === "error" && item.error ? <em> {item.error}</em> : null}
                  </span>
                  {item.status === "error" ? (
                    <button
                      className="attach-action"
                      type="button"
                      aria-label={`Retry ${item.displayName}`}
                      onClick={() => void retryComposerFile(item.localId)}
                    >
                      Retry
                    </button>
                  ) : null}
                  <button
                    className="attach-action"
                    type="button"
                    aria-label={`Remove ${item.displayName}`}
                    onClick={() => void removeComposerFile(item.localId)}
                  >
                    Remove
                  </button>
                </li>
              ))}
            </ul>
          ) : null}
        </div>
        <div className="actions">
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
          <button
            className="key"
            type="button"
            aria-label="Attach"
            disabled={!ready}
            onClick={() => fileInput.current?.click()}
          >
            Attach
          </button>
          <button className="key" type="submit" aria-label="Send" disabled={!canSend}>
            Send
          </button>
          {voiceAvailable ? (
            pendingVoice ? (
              <button className="key" type="button" aria-label="Cancel voice" onClick={onCancelVoice}>
                Cancel
              </button>
            ) : voiceLive ? (
              <button
                className="key"
                type="button"
                aria-label={muted ? "Unmute" : "Mute"}
                onClick={() => onMute(!muted)}
              >
                {muted ? "Unmute" : "Mute"}
              </button>
            ) : (
              <button className="key" type="button" aria-label="Voice" onClick={onVoice}>
                Voice
              </button>
            )
          ) : null}
          {canRetry ? (
            <button className="key" type="button" aria-label="Retry" onClick={onRetry}>
              Retry
            </button>
          ) : null}
          <button className="key" type="button" aria-label="End" onClick={onEnd}>
            End
          </button>
        </div>
      </form>
    </div>
  );
}

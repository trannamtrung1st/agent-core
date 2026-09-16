export function Composer({
  draft,
  canSend,
  ready,
  error,
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
      >
        <label className="message-stack">
          <span className="message-label">Message</span>
          <textarea
            className="message-field"
            value={draft}
            onChange={(event) => onDraftChange(event.target.value)}
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
        <div className="actions">
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

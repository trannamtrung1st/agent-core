import { useEffect } from "react";
import { useChatStore } from "../../state/chatStore";
import {
  bootstrap,
  cancelVoice,
  hangUp,
  requestVoice,
  selectAgent,
  sendDraft,
  setDraft,
  setMuted,
  startConversation
} from "../../services/realtime";

export function ChatApp() {
  const state = useChatStore();

  useEffect(() => {
    void bootstrap().catch(() => undefined);
  }, []);

  const pendingVoice = state.mode !== "voice" && (state.pendingMode === "voice" || state.preflightReady);
  const voiceLive = state.mode === "voice" && state.captureLive;
  const canSend = state.connection === "ready" && state.draft.trim().length > 0;

  return (
    <main>
      <header className="app-header">
        <div>
          <h1>Agent Core</h1>
          <p data-testid="profile">Profile: Synthetic</p>
        </div>
        <p data-testid="connection" className="status">
          {pendingVoice ? "Starting voice…" : connectionLabel(state.connection, voiceLive && !state.muted)}
        </p>
      </header>

      {state.sessionId == null ? (
        <section className="picker">
          <label>
            Identity
            <select
              value={state.selectedAgentId}
              onChange={(event) => selectAgent(event.target.value)}
              aria-label="Identity"
            >
              {state.agents.map((agent) => (
                <option key={agent.id} value={agent.id}>
                  {agent.name} — {agent.role}
                </option>
              ))}
            </select>
          </label>
          <button type="button" aria-label="Start conversation" onClick={() => void startConversation()}>
            Start conversation
          </button>
        </section>
      ) : (
        <>
          <p className="agent-line">
            {state.agentName} · {state.agentRole}
          </p>
          <ol className="transcript" aria-live="polite">
            {state.entries.map((entry) => (
              <li key={entry.entryId} data-role={entry.role}>
                <strong>{entry.role === "user" ? "You" : state.agentName || "Agent"}</strong>
                <span>{entry.text}</span>
                {entry.status === "interrupted" || entry.status === "failed" ? (
                  <em>{entry.status}</em>
                ) : null}
              </li>
            ))}
          </ol>
          {state.error ? (
            <p className="error" role="alert">
              {state.error}
            </p>
          ) : null}
          <form
            className="composer"
            onSubmit={(event) => {
              event.preventDefault();
              void sendDraft();
            }}
          >
            <label>
              Message
              <textarea
                value={state.draft}
                onChange={(event) => setDraft(event.target.value)}
                disabled={state.connection !== "ready"}
                rows={3}
              />
            </label>
            <div className="actions">
              <button type="submit" aria-label="Send" disabled={!canSend}>
                Send
              </button>
              {state.voiceAvailable ? (
                pendingVoice ? (
                  <button type="button" aria-label="Cancel voice" onClick={() => void cancelVoice()}>
                    Cancel
                  </button>
                ) : voiceLive ? (
                  <button type="button" aria-label={state.muted ? "Unmute" : "Mute"} onClick={() => void setMuted(!state.muted)}>
                    {state.muted ? "Unmute" : "Mute"}
                  </button>
                ) : (
                  <button type="button" aria-label="Voice" onClick={() => void requestVoice()}>
                    Voice
                  </button>
                )
              ) : null}
              <button type="button" aria-label="End" onClick={() => void hangUp()}>
                End
              </button>
            </div>
          </form>
        </>
      )}
    </main>
  );
}

function connectionLabel(connection: string, voiceLive: boolean): string {
  if (connection === "reconnecting") {
    return "Reconnecting";
  }

  if (connection === "failed") {
    return "Connection failed. Check the network and try again.";
  }

  if (connection === "ready") {
    return voiceLive ? "Listening" : "Ready";
  }

  return connection;
}

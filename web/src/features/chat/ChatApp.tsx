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
  startConversation
} from "../../services/realtime";

export function ChatApp() {
  const state = useChatStore();

  useEffect(() => {
    void bootstrap().catch(() => undefined);
  }, []);

  const pendingVoice = state.mode !== "voice" && (state.pendingMode === "voice" || state.preflightReady);
  const canSend = state.connection === "ready" && state.draft.trim().length > 0;

  return (
    <main>
      <header className="app-header">
        <div>
          <h1>Agent Core</h1>
          <p data-testid="profile">Profile: Synthetic</p>
        </div>
        <p data-testid="connection" className="status">
          {pendingVoice ? "Starting voice…" : connectionLabel(state.connection, state.mode)}
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
          <button type="button" onClick={() => void startConversation()}>
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
              <button type="submit" disabled={!canSend}>
                Send
              </button>
              {state.voiceAvailable ? (
                pendingVoice ? (
                  <button type="button" onClick={() => void cancelVoice()}>
                    Cancel
                  </button>
                ) : (
                  <button type="button" onClick={() => void requestVoice()}>
                    Voice
                  </button>
                )
              ) : null}
              <button type="button" onClick={() => void hangUp()}>
                End
              </button>
            </div>
          </form>
        </>
      )}
    </main>
  );
}

function connectionLabel(connection: string, mode: string): string {
  if (connection === "reconnecting") {
    return "Reconnecting";
  }

  if (connection === "ready") {
    return mode === "voice" ? "Voice" : "Ready";
  }

  return connection;
}

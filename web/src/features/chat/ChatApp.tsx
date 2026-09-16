import { useEffect, useLayoutEffect, useState } from "react";
import { useSessionStore } from "../../state/sessionStore";
import {
  bootstrap,
  cancelVoice,
  hangUp,
  reportCommittedEntries,
  requestVoice,
  retryConnection,
  selectAgent,
  sendDraft,
  setDraft,
  setMuted,
  startConversation
} from "../../services/realtime";
import { Composer } from "./Composer";
import { Hud } from "./Hud";
import { IdentityPicker } from "./IdentityPicker";
import { conversationStatus, conversationStatusTone } from "./statusLabel";
import { Transcript } from "./Transcript";

export function ChatApp() {
  const state = useSessionStore();
  const [profile, setProfile] = useState("");

  useEffect(() => {
    void bootstrap()
      .then(setProfile)
      .catch(() => undefined);
  }, []);

  useLayoutEffect(() => {
    reportCommittedEntries(state.entries);
  }, [state.entries]);

  const pendingVoice = state.mode !== "voice" && (state.pendingMode === "voice" || state.preflightReady);
  const voiceLive = state.mode === "voice" && state.captureLive;
  const canSend = state.connection === "ready" && state.draft.trim().length > 0;
  const inSession = state.sessionId != null;
  const connectionText = conversationStatus({
    connection: state.connection,
    pendingVoice,
    voiceLive,
    sessionStatus: state.status,
    inputState: state.inputState,
    outputState: state.outputState,
    liveResponseId: state.liveResponseId
  });

  return (
    <main className={inSession ? "field field-session" : "field field-picker"}>
      <div className="field-texture" aria-hidden="true" />
      <Hud
        profile={profile}
        connectionText={connectionText}
        connectionTone={conversationStatusTone(connectionText)}
        identity={inSession ? { name: state.agentName, role: state.agentRole } : null}
      />
      {inSession ? (
        <>
          <Transcript agentName={state.agentName} entries={state.entries} />
          <Composer
            draft={state.draft}
            canSend={canSend}
            ready={state.connection === "ready"}
            error={state.error}
            voiceAvailable={state.voiceAvailable}
            pendingVoice={pendingVoice}
            voiceLive={voiceLive}
            muted={state.muted}
            onDraftChange={setDraft}
            onSend={() => void sendDraft()}
            onVoice={() => void requestVoice()}
            onCancelVoice={() => void cancelVoice()}
            onMute={(muted) => void setMuted(muted)}
            canRetry={state.connection === "failed"}
            onRetry={() => void retryConnection()}
            onEnd={() => void hangUp()}
          />
        </>
      ) : (
        <IdentityPicker
          agents={state.agents}
          selectedAgentId={state.selectedAgentId}
          error={state.error}
          onSelect={selectAgent}
          onStart={() => void startConversation()}
        />
      )}
    </main>
  );
}

import { useEffect, useLayoutEffect, useState } from "react";
import { Button, Drawer, Layout } from "antd";
import { useSessionStore } from "../../state/sessionStore";
import {
  beginNewChat,
  bootstrap,
  cancelVoice,
  composerSendEnabled,
  hangUp,
  openCatalogSession,
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
import { SessionRail } from "./SessionRail";
import { conversationStatus, conversationStatusTone } from "./statusLabel";
import { Transcript } from "./Transcript";

const { Header, Sider, Content } = Layout;
const NARROW_QUERY = "(max-width: 767px)";

function useNarrowLayout() {
  const [isNarrow, setIsNarrow] = useState(() => window.matchMedia(NARROW_QUERY).matches);

  useEffect(() => {
    const media = window.matchMedia(NARROW_QUERY);
    const onChange = () => setIsNarrow(media.matches);
    onChange();
    media.addEventListener("change", onChange);
    return () => media.removeEventListener("change", onChange);
  }, []);

  return isNarrow;
}

export function ChatApp() {
  const state = useSessionStore();
  const [profile, setProfile] = useState("");
  const [sessionsOpen, setSessionsOpen] = useState(false);
  const isNarrow = useNarrowLayout();

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
  const canSend = composerSendEnabled();
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

  function handleNewChat() {
    setSessionsOpen(false);
    void beginNewChat();
  }

  function handleOpen(item: Parameters<typeof openCatalogSession>[0]) {
    setSessionsOpen(false);
    void openCatalogSession(item);
  }

  const rail = (
    <SessionRail
      items={state.catalogItems}
      agents={state.agents}
      activeSessionId={state.sessionId}
      includeArchived={state.catalogIncludeArchived}
      hasMore={state.catalogHasMore}
      capabilityLost={state.catalogCapabilityLost}
      error={state.catalogError}
      mutation={state.catalogMutation}
      onNewChat={handleNewChat}
      onOpen={handleOpen}
    />
  );

  return (
    <Layout className="app-layout">
      {isNarrow ? null : (
        <Sider className="app-sider" theme="light" width={280}>
          <div className="app-sider-inner">{rail}</div>
        </Sider>
      )}
      <Layout>
        <Header className="app-header">
          <Hud
            profile={profile}
            connectionText={connectionText}
            connectionTone={conversationStatusTone(connectionText)}
            identity={inSession ? { name: state.agentName, role: state.agentRole } : null}
            sessionsToggle={
              isNarrow ? (
                <Button aria-label="Open sessions" onClick={() => setSessionsOpen(true)}>
                  Sessions
                </Button>
              ) : null
            }
          />
        </Header>
        <Content className="app-content">
          {inSession ? (
            <div className="conversation-pane">
              <div className="conversation-scroll">
                <Transcript
                  agentName={state.agentName}
                  sessionId={state.sessionId}
                  entries={state.entries}
                  connection={state.connection}
                />
              </div>
              <div className="conversation-composer">
                <Composer
                  draft={state.draft}
                  canSend={canSend}
                  ready={state.connection === "ready"}
                  error={state.error}
                  pendingAttachments={state.pendingAttachments}
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
              </div>
            </div>
          ) : (
            <div className="picker-pane">
              <IdentityPicker
                agents={state.agents}
                selectedAgentId={state.selectedAgentId}
                error={state.error}
                onSelect={selectAgent}
                onStart={() => void startConversation()}
              />
            </div>
          )}
        </Content>
      </Layout>
      {isNarrow ? (
        <Drawer
          title="Sessions"
          placement="left"
          size={320}
          open={sessionsOpen}
          onClose={() => setSessionsOpen(false)}
        >
          {rail}
        </Drawer>
      ) : null}
    </Layout>
  );
}

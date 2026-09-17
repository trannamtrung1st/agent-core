import { useEffect, useLayoutEffect, useState } from "react";
import { App as AntApp, Alert, Button, Drawer, Flex, Layout, Typography } from "antd";
import { MenuOutlined, PlusOutlined } from "@ant-design/icons";
import { isReadonlySession, useSessionStore } from "../../state/sessionStore";
import {
  beginNewChat,
  bootstrap,
  cancelVoice,
  clearRouteNotice,
  navigateFromBrowserHistory,
  composerSendEnabled,
  hangUp,
  openCatalogSession,
  reportCommittedEntries,
  requestVoice,
  retryConnection,
  selectAgent,
  sendDraft,
  setDraft,
  setMuted
} from "../../services/realtime";
import { AgentPicker } from "./AgentPicker";
import { mapAgentActivity, conversationStatusLabel, conversationStatusTone } from "./activityState";
import { ChatHeader } from "./ChatHeader";
import { Composer } from "./Composer";
import { Conversation } from "./Conversation";
import { SessionRail } from "./SessionRail";

const { Header, Sider, Content } = Layout;
const NARROW_QUERY = "(max-width: 767px)";
const TABLET_QUERY = "(max-width: 1199px)";

function useViewport() {
  const [isNarrow, setIsNarrow] = useState(() => window.matchMedia(NARROW_QUERY).matches);
  const [isTablet, setIsTablet] = useState(() => window.matchMedia(TABLET_QUERY).matches);

  useEffect(() => {
    const narrow = window.matchMedia(NARROW_QUERY);
    const tablet = window.matchMedia(TABLET_QUERY);
    const onChange = () => {
      setIsNarrow(narrow.matches);
      setIsTablet(tablet.matches);
    };
    onChange();
    narrow.addEventListener("change", onChange);
    tablet.addEventListener("change", onChange);
    window.addEventListener("resize", onChange);
    return () => {
      narrow.removeEventListener("change", onChange);
      tablet.removeEventListener("change", onChange);
      window.removeEventListener("resize", onChange);
    };
  }, []);

  return { isNarrow, siderWidth: isTablet ? 240 : 280 };
}

export function ChatApp() {
  const state = useSessionStore();
  const [profile, setProfile] = useState("");
  const [sessionsOpen, setSessionsOpen] = useState(false);
  const { isNarrow, siderWidth } = useViewport();

  useEffect(() => {
    void bootstrap()
      .then(setProfile)
      .catch(() => undefined);
  }, []);

  useEffect(() => {
    const onPopState = () => {
      void navigateFromBrowserHistory();
    };
    window.addEventListener("popstate", onPopState);
    return () => window.removeEventListener("popstate", onPopState);
  }, []);

  useLayoutEffect(() => {
    reportCommittedEntries(state.entries);
  }, [state.entries]);

  const pendingVoice = state.mode !== "voice" && (state.pendingMode === "voice" || state.preflightReady);
  const voiceLive = state.mode === "voice" && state.captureLive;
  const canSend = composerSendEnabled();
  const inSession = state.sessionId != null;
  const readonly = isReadonlySession(state);
  const selectedAgent = state.agents.find((agent) => agent.id === state.selectedAgentId) ?? state.agents[0];
  const liveAssistant = state.entries.find(
    (entry) => entry.responseId === state.liveResponseId && entry.role === "assistant"
  );
  const statusSource = {
    connection: state.connection,
    pendingVoice,
    voiceLive,
    sessionStatus: state.status,
    inputState: state.inputState,
    outputState: state.outputState,
    liveResponseId: state.liveResponseId,
    liveAssistantText: liveAssistant?.text,
    liveAssistantHasContent: Boolean(liveAssistant?.blocks?.length)
  };
  const connectionText = conversationStatusLabel(statusSource);
  const activity = mapAgentActivity(statusSource);
  const connectionTone = conversationStatusTone(connectionText);
  const failedAlertTitle = readonly && state.error ? state.error : connectionText;
  const agentName = inSession ? state.agentName : selectedAgent?.name ?? "Agent Core";
  const composerReady = !readonly && (state.connection === "ready" || (!inSession && state.connection === "idle"));
  const voiceAvailable = inSession ? state.voiceAvailable : Boolean(selectedAgent?.voiceAvailable);
  const headerTimestamp = inSession
    ? state.entries.at(-1)?.createdAt
      ?? state.catalogItems.find((item) => item.sessionId === state.sessionId)?.updatedAt
      ?? null
    : null;

  function handleNewChat(options?: { urlMode?: "push" | "replace" }) {
    setSessionsOpen(false);
    void beginNewChat(options);
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
      showHeading={!isNarrow}
      showNewChat={!isNarrow}
      onNewChat={handleNewChat}
      onOpen={handleOpen}
    />
  );

  return (
    <AntApp className="antd-root" message={{ duration: 3, maxCount: 3 }}>
      <Layout className="app-layout">
        {isNarrow ? null : (
          <Sider className="app-sider" theme="light" width={siderWidth}>
            <div className="app-sider-inner">
              <div className="app-sider-brand">
                <Typography.Title level={1} className="app-sider-title">
                  Agent Core
                </Typography.Title>
              </div>
              {rail}
            </div>
          </Sider>
        )}
        <Layout>
          <Header className="app-header">
            <ChatHeader
              title={inSession ? state.agentName || "Agent" : "New chat"}
              subtitle={inSession ? state.agentRole : null}
              timestamp={headerTimestamp}
              sessionsToggle={
                isNarrow ? (
                  <Button
                    type="text"
                    aria-label="Open chats"
                    icon={<MenuOutlined />}
                    onClick={() => setSessionsOpen(true)}
                  />
                ) : null
              }
              inSession={inSession && !readonly}
              onEnd={() => void hangUp()}
            />
            <Flex align="center" gap={8} className="chat-header-meta">
              <Typography.Text data-testid="profile" type="secondary" className="chat-header-profile">
                Profile: {profile || "…"}
              </Typography.Text>
              <Typography.Text
                data-testid="connection"
                type={connectionTone === "alarm" ? "danger" : "secondary"}
                className="chat-header-status"
              >
                {connectionText}
              </Typography.Text>
            </Flex>
          </Header>
          <Content className="app-content">
            {state.routeNotice ? (
              <Alert
                type="info"
                showIcon
                closable
                className="connection-alert"
                title={state.routeNotice}
                onClose={clearRouteNotice}
              />
            ) : null}
            {state.connection === "failed" ? (
              <Alert
                type="error"
                showIcon
                className="connection-alert"
                title={failedAlertTitle}
                action={
                  <Button size="small" aria-label="Retry" onClick={() => void retryConnection()}>
                    Retry
                  </Button>
                }
              />
            ) : null}
            <div className="conversation-pane">
              <div className="conversation-scroll">
                <div className="conversation-column">
                  <Conversation
                    agentName={state.agentName}
                    sessionId={state.sessionId}
                    entries={state.entries}
                    activity={inSession ? activity : { kind: "idle" }}
                    voiceAvailable={voiceAvailable}
                    empty={
                      inSession ? undefined : (
                        <div className="new-chat-intro">
                          <Typography.Title level={2} className="new-chat-title">
                            What do you want to work on?
                          </Typography.Title>
                          <AgentPicker
                            agents={state.agents}
                            selectedAgentId={state.selectedAgentId}
                            error={state.error}
                            onSelect={selectAgent}
                          />
                        </div>
                      )
                    }
                  />
                </div>
              </div>
              <div className="conversation-composer">
                <div className="conversation-column">
                  {readonly ? (
                    <Typography.Text type="secondary" className="conversation-ended-note">
                      This conversation has ended.
                    </Typography.Text>
                  ) : (
                    <Composer
                      draft={state.draft}
                      canSend={canSend}
                      ready={composerReady}
                      error={inSession ? state.error : null}
                      pendingAttachments={state.pendingAttachments}
                      voiceAvailable={voiceAvailable}
                      pendingVoice={pendingVoice}
                      voiceLive={voiceLive}
                      muted={state.muted}
                      placeholder={`Message ${agentName}...`}
                      onDraftChange={setDraft}
                      onSend={() => void sendDraft()}
                      onVoice={() => void requestVoice()}
                      onCancelVoice={() => void cancelVoice()}
                      onMute={(muted) => void setMuted(muted)}
                      canRetry={false}
                      onRetry={() => void retryConnection()}
                    />
                  )}
                </div>
              </div>
            </div>
          </Content>
        </Layout>
        {isNarrow ? (
          <Drawer
            title={<span id="session-rail-drawer-title">Chats</span>}
            aria-labelledby="session-rail-drawer-title"
            placement="left"
            size={320}
            open={sessionsOpen}
            onClose={() => setSessionsOpen(false)}
            extra={
              <Button
                type="text"
                aria-label="Start a new chat"
                icon={<PlusOutlined />}
                onClick={() => handleNewChat()}
                disabled={state.catalogMutation != null}
              >
                New chat
              </Button>
            }
            styles={{ body: { padding: 0, height: "100%" } }}
          >
            {rail}
          </Drawer>
        ) : null}
      </Layout>
    </AntApp>
  );
}

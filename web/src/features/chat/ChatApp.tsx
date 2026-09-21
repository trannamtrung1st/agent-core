import { useEffect, useLayoutEffect, useState } from "react";
import { App as AntApp, Alert, Button, Drawer, Flex, Layout, Typography } from "antd";
import { MenuOutlined, PlusOutlined } from "@ant-design/icons";
import { isReadonlySession, isSessionModelBusy, useSessionStore } from "../../state/sessionStore";
import {
  beginNewChat,
  bootstrap,
  cancelVoice,
  clearRouteNotice,
  navigateFromBrowserHistory,
  composerSendEnabled,
  composerSendLabel,
  composerStopEnabled,
  hangUp,
  loadOlderHistory,
  openCatalogSession,
  reportCommittedEntries,
  requestVoice,
  retryConnection,
  resumePausedSession,
  selectAgent,
  sendDraft,
  applySpeechLocale,
  applySessionModel,
  cancelRenderedResponse,
  respondToApproval,
  setDraft,
  setMuted
} from "../../services/realtime";
import { AgentPicker } from "./AgentPicker";
import { SpeechLocalePicker, speechLocaleSelectValue } from "./SpeechLocalePicker";
import {
  ModelPicker,
  effortSelectValue,
  modelSelectValue,
  nextEffortForModel,
  selectedCatalogModel,
  wireModelSelectionKey
} from "./ModelPicker";
import { imageModelCompatibility } from "./imageModelCompatibility";
import { mapAgentActivity, conversationStatusLabel, conversationStatusTone, pausedSessionMessage } from "./activityState";
import { terminalSessionNote } from "./sessionLifecycle";
import { ChatHeader } from "./ChatHeader";
import { Composer } from "./Composer";
import { Conversation } from "./Conversation";
import { ApprovalModal } from "./ApprovalModal";
import { SessionFailureAlert } from "./SessionFailureAlert";
import { SessionRail } from "./SessionRail";
import { voiceControlEnabled } from "../../speech/voiceEnablement";
import { clientRecognitionSupported, clientSynthesisSupported } from "../../speech/clientSpeechSupport";

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

  const voiceModeActive = state.mode === "voice";
  const voiceInputLive = voiceModeActive && state.captureLive;
  const voiceInputBlocked = state.clientTranscriptBlocked;
  const pendingVoice = state.mode !== "voice" && (state.pendingMode === "voice" || state.preflightReady);
  const voiceLive = voiceInputLive;
  const canSend = composerSendEnabled();
  const canStop = composerStopEnabled();
  const sendLabel = composerSendLabel();
  const inSession = state.sessionId != null;
  const readonly = isReadonlySession(state);
  const modelBusy = isSessionModelBusy(state);
  const modelValue = modelSelectValue(
    state.sessionModelKey,
    state.pendingModelKey,
    inSession,
    state.modelCatalogDefaultKey
  );
  const imageIncompatibility = imageModelCompatibility({
    models: state.modelCatalog,
    modelValue,
    defaultKey: state.modelCatalogDefaultKey,
    pendingAttachments: state.pendingAttachments,
    pendingSendQueue: state.pendingSendQueue
  });
  const selectedModel = selectedCatalogModel(state.modelCatalog, modelValue, state.modelCatalogDefaultKey);
  const effortValue = effortSelectValue(
    selectedModel,
    state.sessionModelEffort,
    state.pendingReasoningEffort,
    inSession
  );
  const modelDisabled = readonly || modelBusy;
  const changeModel = (key: string) => {
    void applySessionModel(
      wireModelSelectionKey(key, state.modelCatalogDefaultKey, inSession),
      nextEffortForModel(state.modelCatalog, key, state.modelCatalogDefaultKey, effortValue)
    );
  };
  const changeEffort = (effort: string | null) => {
    void applySessionModel(
      wireModelSelectionKey(modelValue, state.modelCatalogDefaultKey, inSession),
      effort
    );
  };
  const modelPicker = (
    <ModelPicker
      layout="row"
      models={state.modelCatalog}
      defaultKey={state.modelCatalogDefaultKey}
      modelValue={modelValue}
      effortValue={effortValue}
      disabled={inSession ? modelDisabled : false}
      onModelChange={changeModel}
      onEffortChange={changeEffort}
    />
  );
  const selectedAgent = state.agents.find((agent) => agent.id === state.selectedAgentId) ?? state.agents[0];
  const liveAssistant = state.entries.find(
    (entry) => entry.responseId === state.liveResponseId && entry.role === "assistant"
  );
  const statusSource = {
    connection: state.connection,
    pendingVoice,
    voiceLive,
    clientTranscriptBlocked: state.clientTranscriptBlocked,
    sessionStatus: state.status,
    lifecycleStatus: state.lifecycleStatus,
    inputState: state.inputState,
    outputState: state.outputState,
    liveResponseId: state.liveResponseId,
    liveUserTranscript: state.liveUserTranscript,
    liveAssistantText: liveAssistant?.text,
    liveAssistantHasContent: Boolean(liveAssistant?.blocks?.length),
    connectionError: state.error,
    activeProgress: state.activeProgress
  };
  const connectionText = conversationStatusLabel(statusSource);
  const profileText = `Profile: ${profile || "…"}`;
  const activity = mapAgentActivity(statusSource);
  const connectionTone = conversationStatusTone(connectionText);
  const failedAlertTitle = readonly && state.error ? state.error : connectionText;
  const sessionFailure = state.sessionError ?? state.error;
  const agentName = inSession ? state.agentName : selectedAgent?.name ?? "Agent Core";
  const composerReady = !readonly
    && state.status !== "paused"
    && (state.connection === "ready" || (!inSession && state.connection === "idle"));
  const voiceAvailable =
    voiceControlEnabled({
      voiceAvailable: inSession ? state.voiceAvailable : Boolean(selectedAgent?.voiceAvailable),
      inputTransport: state.sttTransport,
      outputTransport: state.ttsTransport,
      recognitionSupported: clientRecognitionSupported(),
      synthesisSupported: clientSynthesisSupported()
    })
    && (!inSession || state.connection === "ready");
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
      <ApprovalModal
        approval={state.pendingApproval}
        onApprove={() => void respondToApproval("approve")}
        onReject={() => void respondToApproval("reject")}
      />
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
              speechLocale={
                inSession && !readonly ? (
                  <SpeechLocalePicker
                    layout="row"
                    value={speechLocaleSelectValue(
                      state.speechLocaleSource,
                      state.speechLocaleOverride,
                      state.pendingSpeechLocale,
                      true
                    )}
                    disabled={readonly}
                    onChange={(locale) => void applySpeechLocale(locale)}
                  />
                ) : null
              }
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
              <Typography.Text
                data-testid="profile"
                type="secondary"
                className="chat-header-profile"
                ellipsis={{ tooltip: profileText }}
              >
                {profileText}
              </Typography.Text>
              <Typography.Text
                data-testid="connection"
                type={connectionTone === "alarm" ? "danger" : "secondary"}
                className="chat-header-status"
                ellipsis={{ tooltip: connectionText }}
              >
                {connectionText}
              </Typography.Text>
            </Flex>
          </Header>
          <Content className="app-content">
            {state.routeNotice || state.connection === "failed" ? (
              <div className="conversation-column conversation-banners">
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
                  <SessionFailureAlert
                    error={sessionFailure ?? failedAlertTitle}
                    fatal={state.errorFatal}
                    className="connection-alert"
                    action={
                      <Button size="small" aria-label="Retry" onClick={() => void retryConnection()}>
                        Retry
                      </Button>
                    }
                  />
                ) : null}
              </div>
            ) : null}
            <div className="conversation-pane">
              <div className="conversation-scroll">
                <div className="conversation-column">
                  <Conversation
                    agentName={state.agentName}
                    sessionId={state.sessionId}
                    entries={state.entries}
                    liveUserTranscript={state.liveUserTranscript}
                    activity={inSession ? activity : { kind: "idle" }}
                    voiceAvailable={voiceAvailable}
                    sttTransport={state.sttTransport}
                    hasOlder={state.historyHasOlder}
                    olderLoading={state.historyOlderLoading}
                    onLoadOlder={() => void loadOlderHistory()}
                    empty={
                      inSession ? undefined : (
                        <div className="new-chat-intro">
                          <Typography.Title level={2} className="new-chat-title">
                            What do you want to work on?
                          </Typography.Title>
                          <AgentPicker
                            agents={state.agents}
                            selectedAgentId={state.selectedAgentId}
                            error={state.sessionError ?? state.error}
                            speechLocale={speechLocaleSelectValue(
                              state.speechLocaleSource,
                              state.speechLocaleOverride,
                              state.pendingSpeechLocale,
                              false
                            )}
                            onSelect={selectAgent}
                            onSpeechLocaleChange={(locale) => void applySpeechLocale(locale)}
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
                      {terminalSessionNote(state.lifecycleStatus)}
                    </Typography.Text>
                  ) : state.status === "paused" ? (
                    <Flex
                      align="center"
                      justify="center"
                      wrap="wrap"
                      gap={16}
                      className="conversation-paused-note"
                    >
                      <Typography.Text type="secondary">
                        {pausedSessionMessage(state.pauseReason)}
                      </Typography.Text>
                      <Button
                        type="primary"
                        htmlType="button"
                        aria-label="Resume"
                        onClick={() => {
                          void resumePausedSession().then((ok) => {
                            if (!ok) {
                              return;
                            }
                            window.requestAnimationFrame(() => {
                              document.querySelector<HTMLTextAreaElement>('[aria-label="Message"]')?.focus();
                            });
                          });
                        }}
                      >
                        Resume
                      </Button>
                    </Flex>
                  ) : (
                    <Composer
                      draft={state.draft}
                      canSend={canSend}
                      canStop={canStop}
                      sendLabel={sendLabel}
                      pendingSendQueue={state.pendingSendQueue}
                      ready={composerReady}
                      error={
                        inSession && state.connection !== "failed"
                          ? (state.sessionError ?? state.error)
                          : null
                      }
                      pendingAttachments={state.pendingAttachments}
                      voiceAvailable={voiceAvailable}
                      pendingVoice={pendingVoice}
                      voiceModeActive={voiceModeActive}
                      voiceInputLive={voiceInputLive}
                      voiceInputBlocked={voiceInputBlocked}
                      voiceInputHeldForAgentOutput={state.voiceInputHeldForAgentOutput}
                      muted={state.muted}
                      placeholder={`Message ${agentName}...`}
                      onDraftChange={setDraft}
                      onSend={() => void sendDraft()}
                      onStop={() => void cancelRenderedResponse()}
                      onVoice={() => void requestVoice()}
                      onCancelVoice={() => void cancelVoice()}
                      onMute={(muted) => void setMuted(muted)}
                      canRetry={false}
                      onRetry={() => void retryConnection()}
                      modelControls={modelPicker}
                      imageIncompatibilityMessage={
                        imageIncompatibility.incompatible ? imageIncompatibility.message : null
                      }
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

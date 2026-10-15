import { followAppLink } from "../../app/appLink";
import { appMessageConfig } from "../../app/appFeedback";
import { adminHomePath } from "../../app/appRoute";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useWorkReadState } from "./workReadState";
import { messageTimestamp } from "./chatTime";
import { App as AntApp, Alert, Button, Drawer, Flex, Layout, Tooltip, Typography } from "antd";
import { ArrowLeftOutlined, MenuOutlined, PlusOutlined, SettingOutlined } from "@ant-design/icons";
import { isReadonlySession, isSessionModelBusy, useSessionStore } from "../../state/sessionStore";
import {
  beginNewChat,
  bootstrap,
  cancelVoice,
  clearRouteNotice,
  composerSendEnabled,
  composerSendLabel,
  composerStopEnabled,
  hangUp,
  loadOlderHistory,
  openCatalogSession,
  reportCommittedEntries,
  requestVoice,
  resumeCapture,
  resumeLiveSessionForNavigation,
  retryConnection,
  resumePausedSession,
  reloadChatAgentInstances,
  selectChatIdentity,
  sendDraft,
  applySpeechLocale,
  applySessionModel,
  cancelRenderedResponse,
  respondToApproval,
  setDraft,
  setDraftParts,
  addResourceToDraft,
  setMuted
} from "../../services/realtime";
import { AgentPicker } from "./AgentPicker";
import { resolveNewChatIdentityPresentation } from "./chatIdentity";
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
import { getSession, listBackgroundSessions, listSessionAutomations, cancelSessionAutomation, type AgentRun } from "../../services/api";
import { BackgroundWorkDrawer } from "./BackgroundWorkDrawer";
import { SessionSandboxDrawer } from './SessionSandboxDrawer';
import { AutomationDrawer } from "./AutomationDrawer";
import { Composer } from "./Composer";
import { Conversation } from "./Conversation";
import { EmailAccountPicker } from "./EmailAccountPicker";
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

export function ChatApp({ onOpenAdmin, returnToActivity, returnToActivityHref }: { onOpenAdmin?: () => void; returnToActivity?: () => void; returnToActivityHref?: string }) {
  const state = useSessionStore();
  const [profile, setProfile] = useState("");
  const [sessionsOpen, setSessionsOpen] = useState(false);
  const [schedulesOpen, setSchedulesOpen] = useState(false);
  const [workOpen, setWorkOpen] = useState(false);
  const [sandboxOpen, setSandboxOpen] = useState(false);
  const sandboxButtonRef = useRef<HTMLButtonElement>(null);
  const [attentionItems, setAttentionItems] = useState<{ run: AgentRun; delivery?: string }[]>([]);
  const [backgroundInstanceId, setBackgroundInstanceId] = useState<string | null>(null);
  const { isUnread } = useWorkReadState();
  const attentionCount = attentionItems.filter(item => item.delivery === "pending" || item.delivery !== "handled" && isUnread(item.run)).length;

  useEffect(() => {
    if (!state.sessionId) {
      setAttentionItems([]);
      setBackgroundInstanceId(null);
      setWorkOpen(false);
      return;
    }

    let current = true;
    let generation = 0;
    // Keep the drawer mounted while opening another Session owned by the same instance.
    // Its pending Continue action must survive the connection state transition.
    setAttentionItems([]);
    let owner: string | null = null;
    async function refresh() {
      const request = ++generation;
      try {
        owner ??= (await getSession(state.sessionId!)).agentInstanceId ?? null;
        if (!owner || !current) return;
        setBackgroundInstanceId(owner);
        const items: { run: AgentRun; delivery?: string }[] = []; let cursor: string | undefined;
        do {
          const page = await listBackgroundSessions(owner, cursor, 100);
          for (const item of page.items) if (item.initialRun && (item.completionDelivery?.status === "pending" || item.initialRun.outcome?.attentionRequired)) items.push({ run: item.initialRun, delivery: item.completionDelivery?.status });
          cursor = page.hasMore ? page.nextCursor ?? undefined : undefined;
        } while (current && cursor);
        if (current && request === generation) setAttentionItems(items);
      } catch { /* Preserve the last known attention count during a transient failure. */ }
    }
    void refresh();
    const timer = window.setInterval(() => void refresh(), 5_000);
    return () => { current = false; window.clearInterval(timer); };
  }, [state.sessionId]);
  const [scheduleEpoch, setScheduleEpoch] = useState(0);
  const outputStateRef = useRef(state.outputState);
  const { isNarrow, siderWidth } = useViewport();

  useEffect(() => {
    const previous = outputStateRef.current;
    outputStateRef.current = state.outputState;
    if (schedulesOpen && previous !== "idle" && state.outputState === "idle") {
      setScheduleEpoch((value) => value + 1);
    }
  }, [schedulesOpen, state.outputState]);

  useEffect(() => {
    // Internal route changes unmount ChatApp, not its running Session transport.
    // Restore visible-text receipts without reattaching or restarting a Run.
    resumeLiveSessionForNavigation();
    void bootstrap()
      .then(setProfile)
      .catch(() => undefined);
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
  const newChatIdentity = resolveNewChatIdentityPresentation(
    state.newChatIdentityKey,
    state.chatAgentInstances
  );
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
    liveInterruptReason: liveAssistant?.interruptReason ?? null,
    connectionError: state.error,
    activeProgress: state.activeProgress
  };
  const connectionText = conversationStatusLabel(statusSource);
  const profileText = `Profile: ${profile || "…"}`;
  const activity = mapAgentActivity(statusSource);
  const connectionTone = conversationStatusTone(connectionText);
  const failedAlertTitle = readonly && state.error ? state.error : connectionText;
  const sessionFailure = state.sessionError ?? state.error;
  const agentName = inSession ? state.agentName : newChatIdentity?.displayName ?? "Agent Core";
  const composerReady = !readonly
    && state.status !== "paused"
    && (state.connection === "ready" || (!inSession && state.connection === "idle"));
  const voiceAvailable =
    voiceControlEnabled({
      voiceAvailable: inSession ? state.voiceAvailable : Boolean(newChatIdentity?.voiceAvailable),
      inputTransport: state.sttTransport,
      outputTransport: state.ttsTransport,
      recognitionSupported: clientRecognitionSupported(),
      synthesisSupported: clientSynthesisSupported()
    })
    && (!inSession || state.connection === "ready");
  const headerTimestamp = inSession
    ? state.entries.map((entry) => messageTimestamp(entry, state.entries))
        .reduce<string | undefined>((latest, timestamp) =>
          !latest || Date.parse(timestamp) > Date.parse(latest) ? timestamp : latest, undefined)
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
    <AntApp className="antd-root" message={appMessageConfig}>
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
              status={
                <Flex align="center" gap={8} className="chat-header-meta">
                  <Tooltip title={profileText}><Typography.Text
                    data-testid="profile"
                    aria-label={profileText}
                    type="secondary"
                    className="chat-header-profile single-line-ellipsis"
                  >
                    {profile || "…"}
                  </Typography.Text></Tooltip>
                  <Tooltip title={connectionText}><Typography.Text
                    data-testid="connection"
                    type={connectionTone === "alarm" ? "danger" : "secondary"}
                    className={`chat-header-status chat-header-status-${connectionTone} single-line-ellipsis`}
                  >
                    {connectionText}
                  </Typography.Text></Tooltip>
                </Flex>
              }
              additionalActions={onOpenAdmin ? (
                <Tooltip title="Admin">
                  <Button
                    type="text"
                    size="small"
                    className="chat-header-admin"
                    icon={<SettingOutlined />}
                    href={adminHomePath()}
                    onClick={event => followAppLink(event, onOpenAdmin)}
                    aria-label="Open Admin"
                  />
                </Tooltip>
              ) : null}
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
              sessionsToggle={<Flex gap={8}>
                {returnToActivity ? <Tooltip title="Back to Activity"><Button type="text" aria-label="Back to Activity" icon={<ArrowLeftOutlined />} href={returnToActivityHref} onClick={event => followAppLink(event, returnToActivity)} /></Tooltip> : null}
                {isNarrow ? (
                  <Button
                    type="text"
                    aria-label="Open chats"
                    icon={<MenuOutlined />}
                    onClick={() => setSessionsOpen(true)}
                  />
                ) : null}</Flex>
              }
              inSession={inSession && !readonly}
              onSchedules={inSession && !readonly && state.sessionId ? () => setSchedulesOpen(true) : undefined}
              onBackgroundWork={state.sessionId ? () => setWorkOpen(true) : undefined}
              sandboxButtonRef={sandboxButtonRef}
              onSandbox={state.sessionId ? () => setSandboxOpen(true) : undefined}
              attentionCount={attentionCount}
              onEnd={() => void hangUp()}
            />
          </Header>
          <Content className="app-content">
            <EmailAccountPicker sessionId={state.sessionId} connected={state.connection === "ready" && !readonly} />
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
                    sessionId={state.sessionId}
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
                            managedInstances={state.chatAgentInstances}
                            identityKey={state.newChatIdentityKey}
                            error={state.sessionError ?? state.error}
                            speechLocale={speechLocaleSelectValue(
                              state.speechLocaleSource,
                              state.speechLocaleOverride,
                              state.pendingSpeechLocale,
                              false
                            )}
                            managedInstancesError={state.chatAgentInstancesError}
                            managedInstancesLoading={state.chatAgentInstancesLoading}
                            onIdentityChange={selectChatIdentity}
                            onSpeechLocaleChange={(locale) => void applySpeechLocale(locale)}
                            onRetryManagedInstances={() => void reloadChatAgentInstances()}
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
                              document.querySelector<HTMLElement>('[aria-label="Message"]')?.focus();
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
                      parts={state.draftParts}
                      instanceId={inSession ? backgroundInstanceId : state.newChatIdentityKey.startsWith("managed:") ? state.newChatIdentityKey.slice(8) : null}
                      onPartsChange={setDraftParts}
                      canSend={canSend}
                      canStop={canStop}
                      approvalPending={state.pendingApproval !== null}
                      sendLabel={sendLabel}
                      pendingSendQueue={state.pendingSendQueue}
                      ready={composerReady}
                      error={
                        inSession && state.connection !== "failed"
                          ? (state.sessionError ?? state.error)
                          : null
                      }
                      sessionId={state.sessionId}
                      pendingAttachments={state.pendingAttachments}
                      voiceAvailable={voiceAvailable}
                      pendingVoice={pendingVoice}
                      voiceModeActive={voiceModeActive}
                      voiceInputLive={voiceInputLive}
                      voiceInputBlocked={voiceInputBlocked}
                      voiceInputHeldForAgentOutput={state.voiceInputHeldForAgentOutput}
                      captureNeedsResume={voiceModeActive && !state.captureAuthorized}
                      muted={state.muted}
                      placeholder={`Message ${agentName}...`}
                      onDraftChange={setDraft}
                      onSend={(behavior) => void sendDraft(behavior)}
                      onStop={() => void cancelRenderedResponse()}
                      onVoice={() => void requestVoice()}
                      onCancelVoice={() => void cancelVoice()}
                      onMute={(muted) => void setMuted(muted)}
                      onResumeCapture={() => void resumeCapture()}
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
        {backgroundInstanceId ? <BackgroundWorkDrawer instanceId={backgroundInstanceId} open={workOpen} wide={!isNarrow}
          onClose={() => setWorkOpen(false)} onAddReference={(sessionId,label) => addResourceToDraft({kind:"backgroundSession",sessionId},label)} /> : null}
        {state.sessionId && <SessionSandboxDrawer key={state.sessionId} sessionId={state.sessionId} open={sandboxOpen} openerRef={sandboxButtonRef} narrow={isNarrow} readOnly={readonly} onClose={() => setSandboxOpen(false)} />}

        {state.sessionId ? (
          <AutomationDrawer
            sessionId={state.sessionId}
            open={schedulesOpen}
            wide={!isNarrow}
            refreshKey={scheduleEpoch}
            onClose={() => setSchedulesOpen(false)}
            load={listSessionAutomations}
            cancel={cancelSessionAutomation}
          />
        ) : null}
        {isNarrow ? (
          <Drawer
            title={<span id="session-rail-drawer-title">Chats</span>}
            aria-labelledby="session-rail-drawer-title"
            placement="left"
            size={320}
            open={sessionsOpen}
            onClose={() => setSessionsOpen(false)}
            onKeyDown={(event) => {
              if (event.key !== "Escape") {
                return;
              }
              event.stopPropagation();
              setSessionsOpen(false);
            }}
            extra={
              <Button
                type="text"
                aria-label="Start a new chat"
                icon={<PlusOutlined />}
                href="/"
                onClick={event => followAppLink(event, () => handleNewChat())}
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

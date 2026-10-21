import { ArrowLeftOutlined,MenuOutlined,PlusOutlined,SettingOutlined } from "@ant-design/icons";
import { Alert,App as AntApp,Button,Drawer,Flex,Layout,Tooltip,Typography } from "antd";
import { useEffect,useLayoutEffect,useMemo,useRef,useState,type ReactNode } from "react";
import { appMessageConfig } from "../../app/appFeedback";
import { followAppLink } from "../../app/appLink";
import { adminHomePath } from "../../app/appRoute";
import { newChatPath } from '../../app/sessionRoute';
import { legacyActivitySource } from "../../services/activitySource";
import { cancelSessionAutomation,getSession,listSessionAutomations,type AgentRun } from "../../services/api";
import type { TenantContext } from '../../services/identity';
import {
addResourceToDraft,
applySessionModel,
applySpeechLocale,
beginNewChat,
bootstrap,
cancelRenderedResponse,
cancelVoice,
clearRouteNotice,
composerSendEnabled,
composerSendLabel,
composerStopEnabled,
hangUp,
loadMoreChatAgents,
loadOlderHistory,
openCatalogSession,
reloadChatAgentInstances,
reportCommittedEntries,
requestVoice,
respondToApproval,
resumeCapture,
resumeLiveSessionForNavigation,
resumePausedSession,
retryConnection,
searchChatAgents,
selectChatIdentity,
sendDraft,
setDraft,
setDraftParts,
setMuted
} from "../../services/realtime";
import { tenantActivitySource } from "../../services/tenantActivity";
import { tenantCanCreateChat,tenantMayManageSession,tenantRunControl } from '../../services/tenantChat';
import { clientRecognitionSupported,clientSynthesisSupported } from "../../speech/clientSpeechSupport";
import { voiceControlEnabled } from "../../speech/voiceEnablement";
import { isReadonlySession,isSessionModelBusy,useSessionStore } from "../../state/sessionStore";
import { AccessError } from '../access/AccessFeedback';
import { conversationStatusLabel,conversationStatusTone,mapAgentActivity,pausedSessionMessage } from "./activityState";
import { AgentPicker } from "./AgentPicker";
import { ApprovalModal } from "./ApprovalModal";
import { AutomationDrawer } from "./AutomationDrawer";
import { BackgroundWorkDrawer } from "./BackgroundWorkDrawer";
import { ChatHeader } from "./ChatHeader";
import { resolveNewChatIdentityPresentation } from "./chatIdentity";
import { messageTimestamp } from "./chatTime";
import { Composer } from "./Composer";
import { Conversation } from "./Conversation";
import { EmailAccountPicker } from "./EmailAccountPicker";
import { imageModelCompatibility } from "./imageModelCompatibility";
import {
ModelPicker,
effortSelectValue,
modelSelectValue,
nextEffortForModel,
selectedCatalogModel,
wireModelSelectionKey
} from "./ModelPicker";
import { SessionFailureAlert } from "./SessionFailureAlert";
import { terminalSessionNote } from "./sessionLifecycle";
import { SessionRail } from "./SessionRail";
import { SpeechLocalePicker,speechLocaleSelectValue } from "./SpeechLocalePicker";
import { TenantConversationApprovals } from './TenantConversationApprovals';
import { TenantSessionFilesDrawer } from './TenantSessionFilesDrawer';
import { TenantSessionSandboxDrawer } from './TenantSessionSandboxDrawer';
import { useWorkReadState } from "./workReadState";

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

export function ChatApp({ onOpenAdmin, returnToActivity, returnToActivityHref, tenantContext, tenantActions, tenantError }: { onOpenAdmin?: () => void; returnToActivity?: () => void; returnToActivityHref?: string; tenantContext?: TenantContext; tenantActions?: ReactNode; tenantError?: unknown }) {
  const state = useSessionStore();
  const organization = Boolean(tenantContext);
  const [runCanCancel, setRunCanCancel] = useState(false);
  useEffect(() => {
    if (!organization || !state.agentRunId || !state.liveResponseId) { setRunCanCancel(false); return; }
    let current = true; setRunCanCancel(false);
    void tenantRunControl(state.agentRunId).then(result => { if (current) setRunCanCancel(result.canCancel); }).catch(() => { if (current) setRunCanCancel(false); });
    return () => { current = false; };
  }, [organization, state.agentRunId, state.liveResponseId]);
  const [profile, setProfile] = useState("");
  const [bootstrapped, setBootstrapped] = useState(false);
  const [sessionsOpen, setSessionsOpen] = useState(false);
  const [schedulesOpen, setSchedulesOpen] = useState(false);
  const [workOpen, setWorkOpen] = useState(false);
  const [sandboxOpen, setSandboxOpen] = useState(false);
  const [filesOpen, setFilesOpen] = useState(false);
  const filesButtonRef = useRef<HTMLButtonElement>(null);
  useEffect(() => { setFilesOpen(false); }, [state.sessionId, tenantContext?.tenant.tenantId]);
  const sandboxButtonRef = useRef<HTMLButtonElement>(null);
  const [attentionItems, setAttentionItems] = useState<{ run: AgentRun; delivery?: string }[]>([]);
  const [backgroundInstanceId, setBackgroundInstanceId] = useState<string | null>(null);
  const activitySource = useMemo(() => tenantContext && backgroundInstanceId ? tenantActivitySource(tenantContext.tenant.tenantId, backgroundInstanceId, tenantContext.principalId) : legacyActivitySource, [tenantContext?.tenant.tenantId, tenantContext?.principalId, backgroundInstanceId]);
  const workButtonRef = useRef<HTMLButtonElement>(null);
  const { isUnread } = useWorkReadState(tenantContext ? `${tenantContext.tenant.tenantId}:${tenantContext.principalId}` : undefined);
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
          const source = tenantContext ? tenantActivitySource(tenantContext.tenant.tenantId, owner, tenantContext.principalId) : legacyActivitySource;
          const page = await source.listBackground(owner, cursor, 100);
          for (const item of page.items) if (item.initialRun && (item.completionDelivery?.status === "pending" || item.initialRun.outcome?.attentionRequired)) items.push({ run: item.initialRun, delivery: item.completionDelivery?.status });
          cursor = page.hasMore ? page.nextCursor ?? undefined : undefined;
        } while (current && cursor);
        if (current && request === generation) setAttentionItems(items);
      } catch { if (tenantContext && current && request === generation) setAttentionItems([]); }
    }
    void refresh();
    const timer = window.setInterval(() => void refresh(), 5_000);
    return () => { current = false; window.clearInterval(timer); };
  }, [state.sessionId, tenantContext?.tenant.tenantId, tenantContext?.principalId]);
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
      .catch(error => useSessionStore.setState({ chatAgentInstancesError: error instanceof Error ? error.message : "Unable to load Chat.", chatAgentInstancesLoading: false }))
      .finally(() => setBootstrapped(true));
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
  const canStop = composerStopEnabled() && (!organization || runCanCancel);
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
  const modelPicker = organization && inSession ? <Typography.Text type="secondary">{selectedModel?.displayName || state.sessionModelDisplayName || "Conversation model"}</Typography.Text> : (
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
              additionalActions={tenantActions ?? (onOpenAdmin ? (
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
              ) : null)}
              speechLocale={
                inSession && !readonly && !organization ? (
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
              inSession={inSession && !readonly && (!organization || tenantMayManageSession(state.sessionId))}
              endDisabled={organization && state.liveResponseId != null}
              onSchedules={!organization && inSession && !readonly && state.sessionId ? () => setSchedulesOpen(true) : undefined}
              backgroundButtonRef={workButtonRef} onBackgroundWork={state.sessionId ? () => setWorkOpen(true) : undefined}
              filesButtonRef={filesButtonRef}
              onFiles={tenantContext && state.sessionId ? () => setFilesOpen(true) : undefined}
              sandboxButtonRef={sandboxButtonRef}
              onSandbox={state.sessionId ? () => setSandboxOpen(true) : undefined}
              attentionCount={attentionCount}
              onEnd={() => void hangUp()}
            />
          </Header>
          <Content className="app-content">
            {!organization && <EmailAccountPicker sessionId={state.sessionId} connected={state.connection === "ready" && !readonly} />}
            {tenantError !== undefined && <div className="conversation-column conversation-banners"><AccessError error={tenantError} /></div>}
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
                    tenantId={tenantContext?.tenant.tenantId}
                afterEntries={organization && tenantContext && state.sessionId ? <TenantConversationApprovals key={`${tenantContext.tenant.tenantId}:${state.sessionId}`} tenant={tenantContext.tenant.tenantId} organization={tenantContext.tenant.name} session={state.sessionId} /> : undefined}
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
                            organization={organization}
                            onSearch={organization ? query => void searchChatAgents(query) : undefined}
                            onLoadMore={organization ? () => void loadMoreChatAgents() : undefined}
                            hasMore={organization && state.chatAgentHasMore}
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
                            managedInstancesLoading={state.chatAgentInstancesLoading || !bootstrapped}
                            onIdentityChange={selectChatIdentity}
                            onSpeechLocaleChange={(locale) => void applySpeechLocale(locale)}
                            onRetryManagedInstances={() => void reloadChatAgentInstances()}
                          />
                          {organization && state.chatAgentInstances.length > 0 && !tenantCanCreateChat() && <Typography.Paragraph type="secondary">Ask an organization owner for permission to start a conversation.</Typography.Paragraph>}
                        </div>
                      )
                    }
                  />
                </div>
              </div>
              <div className="conversation-composer">
                <div className="conversation-column">
                  {readonly ? (
                    <Flex align="center" justify="center" wrap gap={16}>
                      <Typography.Text type="secondary" className="conversation-ended-note">
                        {terminalSessionNote(state.lifecycleStatus)}
                      </Typography.Text>
                      <Button aria-label="Start another chat" onClick={() => handleNewChat()}>New chat</Button>
                    </Flex>
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
                      plainTextOnly={organization}
                      fileAttachmentsEnabled={organization}
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
        {backgroundInstanceId ? <BackgroundWorkDrawer source={activitySource} instanceId={backgroundInstanceId} open={workOpen} wide={!isNarrow}
          onClose={() => setWorkOpen(false)} afterClose={() => workButtonRef.current?.focus()} onAddReference={!organization ? (sessionId,label) => addResourceToDraft({kind:"backgroundSession",sessionId},label) : undefined} /> : null}
        {tenantContext && state.sessionId && <TenantSessionFilesDrawer key={`files:${tenantContext.tenant.tenantId}:${state.sessionId}`} tenant={tenantContext.tenant.tenantId} organization={tenantContext.tenant.name} sessionId={state.sessionId} open={filesOpen} narrow={isNarrow} openerRef={filesButtonRef} onClose={() => setFilesOpen(false)} />}
        {tenantContext && state.sessionId && <TenantSessionSandboxDrawer key={`${tenantContext.tenant.tenantId}:${state.sessionId}`} tenant={tenantContext.tenant.tenantId} organization={tenantContext.tenant.name} sessionId={state.sessionId} open={sandboxOpen} openerRef={sandboxButtonRef} narrow={isNarrow} onClose={() => setSandboxOpen(false)} />}

        {!organization && state.sessionId ? (
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
                href={newChatPath()}
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

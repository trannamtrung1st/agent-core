import { modelPreferenceKey } from '../../services/modelPreference';
import { BranchesOutlined, AimOutlined, ApartmentOutlined, MenuOutlined, PlusOutlined } from "@ant-design/icons";
import { Alert, App as AntApp, Button, Drawer, Flex, Layout, Segmented, Spin, Switch, Tooltip, Typography, theme } from "antd";
import { lazy, Suspense, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { appMessageConfig } from "../../app/appFeedback";
import { followAppLink } from "../../app/appLink";
import { newChatPath } from '../../app/sessionRoute';
import { getSession, type AgentRun } from "../../services/api";
import type { TenantContext } from '../../services/identity';
import { applySessionModel, beginNewChat, bootstrap, cancelRenderedResponse, cancelVoice, clearRouteNotice, composerSendEnabled, composerSendLabel, composerStopEnabled, expireCurrentChat, hangUp, loadMoreChatAgents, loadOlderHistory, openCatalogSession, reloadChatAgentInstances, reportCommittedEntries, requestVoice, resumeCapture, resumeLiveSessionForNavigation, resumePausedSession, retryConnection, searchChatAgents, selectChatIdentity, sendDraft, setAgentCompletion, setDraft, setDraftParts, setMuted } from "../../services/realtime";
import { tenantActivitySource } from "../../services/tenantActivity";
import { tenantCanCreateChat, tenantMayManageSession, tenantRunControl } from '../../services/tenantChat';
import { clientRecognitionSupported, clientSynthesisSupported } from "../../speech/clientSpeechSupport";
import { voiceControlEnabled } from "../../speech/voiceEnablement";
import { isReadonlySession, isSessionModelBusy, useSessionStore } from "../../state/sessionStore";
import { AccessError } from '../access/AccessFeedback';
import { tenantModelsPath } from '../access/accessRoute';
import { conversationStatusLabel, conversationStatusTone, mapAgentActivity, pausedSessionMessage } from "./activityState";
import { AgentPicker } from "./AgentPicker";
import { BackgroundWorkDrawer } from "./BackgroundWorkDrawer";
import { TaskCenterDrawer } from "./TaskCenterDrawer";
import { SessionGoalsDrawer } from "./SessionGoalsDrawer";
import { ChatHeader } from "./ChatHeader";
import { parseChatIdentityKey, resolveNewChatIdentityPresentation } from "./chatIdentity";
import { messageTimestamp } from "./chatTime";
import { Composer } from "./Composer";
import { Conversation } from "./Conversation";
import { imageModelCompatibility } from "./imageModelCompatibility";
import { INHERIT_MODEL, ModelPicker, effortSelectValue, modelSelectValue, nextEffortForModel, selectedCatalogModel } from "./ModelPicker";
import { modelChoiceLabel } from "../models/modelLabels";
import { SessionFailureAlert } from "./SessionFailureAlert";
import { terminalSessionNote } from "./sessionLifecycle";
import { SessionRail } from "./SessionRail";
import { TenantConversationApprovals } from './TenantConversationApprovals';
import { TenantSessionContext } from './TenantSessionContext';
import { TenantSessionFilesDrawer } from './TenantSessionFilesDrawer';
import { TenantSessionSandboxDrawer } from './TenantSessionSandboxDrawer';
import { useWorkReadState } from "./workReadState";

const SessionWorkflowsDrawer = lazy(() => import('../workflows/SessionWorkflowsDrawer').then(module => ({ default: module.SessionWorkflowsDrawer })));

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

export function ChatApp({ tenantContext, tenantActions, tenantError }: { tenantContext: TenantContext; tenantActions?: ReactNode; tenantError?: unknown }) {
  const state = useSessionStore();
  const expired = state.lifecycleStatus === "expired";
  useEffect(() => {
    if (!state.sessionId || !state.retentionDueAtUtc) return;
    const sessionId = state.sessionId; const deadline = new Date(state.retentionDueAtUtc).getTime();
    if (!Number.isFinite(deadline)) return;
    let timer: ReturnType<typeof setTimeout>;
    const check = () => {
      clearTimeout(timer);
      const remaining = deadline - Date.now();
      if (remaining <= 0) void expireCurrentChat(sessionId);
      else timer = setTimeout(check, Math.min(remaining, 2_147_483_647));
    };
    check(); document.addEventListener('visibilitychange', check);
    return () => { clearTimeout(timer); document.removeEventListener('visibilitychange', check); };
  }, [state.sessionId, state.retentionDueAtUtc]);

  const { token } = theme.useToken();
  const [runCanCancel, setRunCanCancel] = useState(false);
  useEffect(() => {
    if (!state.agentRunId || !state.liveResponseId) { setRunCanCancel(false); return; }
    let current = true; setRunCanCancel(false);
    void tenantRunControl(state.agentRunId).then(result => { if (current) setRunCanCancel(result.canCancel); }).catch(() => { if (current) setRunCanCancel(false); });
    return () => { current = false; };
  }, [state.agentRunId, state.liveResponseId]);
  const [profile, setProfile] = useState("");
  const [bootstrapped, setBootstrapped] = useState(false);
  const [sessionsOpen, setSessionsOpen] = useState(false);

  const [workflowsOpen, setWorkflowsOpen] = useState(false); const workflowsButtonRef = useRef<HTMLButtonElement>(null);
  const [tasksOpen, setTasksOpen] = useState(false); const tasksButtonRef = useRef<HTMLButtonElement>(null);
  const requestedTaskId = new URLSearchParams(window.location.search).get("task") ?? undefined;
  useEffect(() => { setTasksOpen(!expired && !!requestedTaskId); }, [state.sessionId, tenantContext.tenant.tenantId, expired, requestedTaskId]);
  const [goalsOpen, setGoalsOpen] = useState(false);
  const requestedGoalId = new URLSearchParams(window.location.search).get("goal");
  const goalsButtonRef = useRef<HTMLButtonElement>(null);
  const [workOpen, setWorkOpen] = useState(false);
  const [sandboxOpen, setSandboxOpen] = useState(false);
  const [filesOpen, setFilesOpen] = useState(false);
  const filesButtonRef = useRef<HTMLButtonElement>(null);
  useEffect(() => { setFilesOpen(false); setSandboxOpen(false); setWorkflowsOpen(false); setGoalsOpen(!expired && !!requestedGoalId); if (expired) setWorkOpen(false); }, [state.sessionId, tenantContext.tenant.tenantId, expired, requestedGoalId]);
  const sandboxButtonRef = useRef<HTMLButtonElement>(null);
  const [attentionItems, setAttentionItems] = useState<{ run: AgentRun; delivery?: string }[]>([]);
  const [backgroundInstanceId, setBackgroundInstanceId] = useState<string | null>(null);
  const activitySource = useMemo(() => backgroundInstanceId ? tenantActivitySource(tenantContext.tenant.tenantId, backgroundInstanceId, tenantContext.principalId) : null, [tenantContext.tenant.tenantId, tenantContext.principalId, backgroundInstanceId]);
  const workButtonRef = useRef<HTMLButtonElement>(null);
  const { isUnread } = useWorkReadState(`${tenantContext.tenant.tenantId}:${tenantContext.principalId}`);
  const attentionCount = attentionItems.filter(item => item.delivery === "pending" || item.delivery !== "handled" && isUnread(item.run)).length;

  useEffect(() => {
    if (!state.sessionId || expired) {
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
          const source = tenantActivitySource(tenantContext.tenant.tenantId, owner, tenantContext.principalId);
          const page = await source.listBackground(owner, cursor, 100);
          for (const item of page.items) if (item.initialRun && (item.completionDelivery?.status === "pending" || item.initialRun.outcome?.attentionRequired)) items.push({ run: item.initialRun, delivery: item.completionDelivery?.status });
          cursor = page.hasMore ? page.nextCursor ?? undefined : undefined;
        } while (current && cursor);
        if (current && request === generation) setAttentionItems(items);
      } catch { if (current && request === generation) setAttentionItems([]); }
    }
    void refresh();
    const timer = window.setInterval(() => void refresh(), 5_000);
    return () => { current = false; window.clearInterval(timer); };
  }, [state.sessionId, tenantContext.tenant.tenantId, tenantContext.principalId, expired]);

  const { isNarrow, siderWidth } = useViewport();

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
  const canStop = composerStopEnabled() && (runCanCancel);
  const sendLabel = composerSendLabel();
  const inSession = state.sessionId != null;
  const readonly = isReadonlySession(state);
  const modelBusy = isSessionModelBusy(state);
  const modelValue = modelSelectValue(
    modelPreferenceKey(state.sessionModelPreference, state.sessionOfferingId),
    state.pendingOfferingId,
    inSession,
    state.modelCatalogDefaultOfferingId
  );
  const imageIncompatibility = imageModelCompatibility({
    models: state.modelCatalog,
    modelValue,
    defaultOfferingId: state.modelCatalogDefaultOfferingId,
    pendingAttachments: state.pendingAttachments,
    pendingSendQueue: state.pendingSendQueue
  });
  const selectedModel = selectedCatalogModel(state.modelCatalog, modelValue, state.modelCatalogDefaultOfferingId);
  const effortValue = inSession && state.sessionModelPreference
    ? state.sessionModelPreference.reasoning?.mode === "auto" ? "auto"
      : state.sessionModelPreference.reasoning?.mode === "fixed" ? state.sessionModelPreference.reasoning.effort ?? null
      : state.sessionModelPreference.mode === "inherit" && state.sessionModelPreference.reasoning?.mode === "modelDefault" ? "modelDefault" : null
    : !inSession ? state.pendingReasoningEffort : effortSelectValue(selectedModel, state.sessionModelEffort, state.pendingReasoningEffort, inSession, null);

  const modelDisabled = readonly || modelBusy || state.mode !== "text" || inSession && state.connection !== "ready";
  const changeModel = (key: string) => {
    const choice = key;
    void applySessionModel(
      choice,
      choice === "auto" ? "auto" : choice === INHERIT_MODEL ? null : nextEffortForModel(state.modelCatalog, key, state.modelCatalogDefaultOfferingId, effortValue)
    );
  };
  const inheritedModel = inSession ? state.sessionModelPreference?.mode === "inherit" || state.sessionModelPreference === null && (state.sessionModelSource === "tenantDefault" || state.sessionModelSource === "agentDefault") : modelValue === INHERIT_MODEL;
  const changeEffort = (effort: string | null) => {
    void applySessionModel(
      inheritedModel ? INHERIT_MODEL : modelValue,
      effort
    );
  };
  const modelPicker = inSession && (!tenantMayManageSession(state.sessionId) || state.modelCatalog.length === 0) ? <Typography.Text type="secondary">{state.sessionModelDisplayName || (selectedModel ? modelChoiceLabel(selectedModel) : "Conversation model")}{!selectedModel && !state.modelCatalogLoading ? " · Unavailable" : ""}</Typography.Text> : (
    <ModelPicker
      layout="row"
      models={state.modelCatalog}
      defaultOfferingId={state.modelCatalogDefaultOfferingId}
      defaultSource={state.modelCatalogDefaultSource}
      modelValue={modelValue}
      inheritSelected={inheritedModel}
      unavailableLabel={state.sessionModelDisplayName}
      lastResolved={state.sessionModelObserved && state.sessionModelDisplayName ? `${state.sessionModelDisplayName}${state.sessionModelEffort ? ` · ${state.sessionModelEffort} reasoning` : ""}` : null}
      effortValue={effortValue}
      disabled={inSession ? modelDisabled : state.modelCatalogLoading || !bootstrapped || state.connection !== "idle"}
      onModelChange={changeModel}
      onEffortChange={changeEffort}
    />
  );
  const newChatIdentity = resolveNewChatIdentityPresentation(
    state.newChatIdentityKey,
    state.chatAgentInstances
  );
  const modelSetupReady = bootstrapped && !!newChatIdentity && !state.chatAgentInstancesLoading
    && !state.modelCatalogLoading && !state.modelCatalogError;
  const canConfigureConnection = tenantContext.permissions?.includes('model-connections.read')
    && tenantContext.permissions.includes('model-connections.manage');
  const canConfigureDefault = tenantContext.permissions?.includes('models.read') && tenantContext.permissions.includes('models.manage');
  const modelSetup = !modelSetupReady ? null : state.modelCatalog.length === 0 ? {
    title: 'No model is available for this identity',
    description: canConfigureConnection ? 'Connect a provider, add an enabled model and choose an organization default. Starter presets contain no credentials.' : 'Ask an organization owner to configure an available model. Starter presets contain no credentials.',
    href: canConfigureConnection ? tenantModelsPath(tenantContext.tenant.tenantId, 'connections') : null,
    action: 'Connect a provider'
  } : inheritedModel && !selectedModel && state.modelCatalogDefaultSource !== "auto" ? {
    title: 'Choose a model for this chat',
    description: 'The inherited default is unavailable. Choose an available model in the composer, or ask an organization owner to set a default.',
    href: canConfigureDefault ? tenantModelsPath(tenantContext.tenant.tenantId, 'defaults') : null,
    action: 'Model defaults'
  } : null;
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
              additionalActions={<>{state.sessionId && !expired && <Tooltip title="Workflows"><Button type="text" size="small" className="chat-header-icon-action" aria-label="Workflows" ref={workflowsButtonRef} icon={<BranchesOutlined />} onClick={() => setWorkflowsOpen(true)} /></Tooltip>}{state.sessionId && !expired && <Tooltip title="Tasks"><Button type="text" size="small" className="chat-header-icon-action" aria-label="Tasks" ref={tasksButtonRef} icon={<ApartmentOutlined />} onClick={() => setTasksOpen(true)} /></Tooltip>}{state.sessionId && !expired && <Tooltip title="Session goals"><Button type="text" size="small" className="chat-header-icon-action" aria-label="Session goals" ref={goalsButtonRef} icon={<AimOutlined />} onClick={() => setGoalsOpen(true)} /></Tooltip>}{tenantActions}</>}

              sessionsToggle={<Flex gap={8}>
                {isNarrow ? (
                  <Button
                    type="text"
                    aria-label="Open chats"
                    icon={<MenuOutlined />}
                    onClick={() => setSessionsOpen(true)}
                  />
                ) : null}</Flex>
              }
              inSession={inSession && !readonly && (tenantMayManageSession(state.sessionId))}
              endDisabled={state.liveResponseId != null}
              backgroundButtonRef={workButtonRef} onBackgroundWork={state.sessionId && !expired ? () => setWorkOpen(true) : undefined}
              filesButtonRef={filesButtonRef}
              onFiles={state.sessionId && !expired ? () => setFilesOpen(true) : undefined}
              sandboxButtonRef={sandboxButtonRef}
              onSandbox={state.sessionId && !expired ? () => setSandboxOpen(true) : undefined}
              attentionCount={attentionCount}
              onEnd={() => void hangUp()}
            />
          </Header>
          <Content className="app-content">
            {state.sessionId && !expired && <TenantSessionContext key={`${tenantContext.tenant.tenantId}:${state.sessionId}`} tenant={tenantContext.tenant.tenantId} session={state.sessionId} refreshKey={`${state.agentRunId ?? ''}:${state.liveResponseId ?? ''}`} />}
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
            {inSession && !readonly && tenantMayManageSession(state.sessionId) ? <Flex align="center" wrap gap={token.paddingXS}>
              <Switch size="small" aria-label="Allow agent to complete this chat" checked={state.agentCompletion === 'allowed'} disabled={composerStopEnabled() || state.mode === 'voice'} onChange={allowed => void setAgentCompletion(allowed)} />
              <Typography.Text type="secondary">Allow agent to complete this chat · {state.agentCompletion === 'allowed' ? 'On' : 'Off'}</Typography.Text>
            </Flex> : null}
            {inSession && state.retentionKind === 'temporary' ? <Typography.Text type="secondary" role="status">Temporary · expires {state.retentionDueAtUtc ? new Date(state.retentionDueAtUtc).toLocaleString() : '24 hours after creation'}. Explicit external effects may persist.</Typography.Text> : null}
            <div className="conversation-pane">
              <div className="conversation-scroll">
                <div className="conversation-column">
                  <Conversation key={`${state.sessionId}:${expired}`}
                    activitySource={activitySource ?? undefined}
                    tenantId={tenantContext.tenant.tenantId}
                afterEntries={state.sessionId && !expired ? <TenantConversationApprovals key={`${tenantContext.tenant.tenantId}:${state.sessionId}`} tenant={tenantContext.tenant.tenantId} organization={tenantContext.tenant.name} session={state.sessionId} /> : undefined}
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
                            onSearch={query => void searchChatAgents(query)}
                            onLoadMore={() => void loadMoreChatAgents()}
                            hasMore={state.chatAgentHasMore}
                            managedInstances={state.chatAgentInstances}
                            identityKey={state.newChatIdentityKey}
                            error={state.modelCatalogError ?? state.sessionError ?? state.error}

                            managedInstancesError={state.chatAgentInstancesError}
                            managedInstancesLoading={state.chatAgentInstancesLoading || !bootstrapped}
                            onIdentityChange={selectChatIdentity}
                            onRetryManagedInstances={() => void reloadChatAgentInstances()}
                          />
                          <Flex vertical gap={token.paddingXS}>
                            <Segmented aria-label="Chat retention" value={state.newChatRetentionKind} options={[{ label: 'Saved', value: 'saved' }, { label: 'Temporary', value: 'temporary' }]}
                              onChange={value => useSessionStore.setState({ newChatRetentionKind: value as 'saved' | 'temporary' })} />
                            {state.newChatRetentionKind === 'temporary' ? <Typography.Text type="secondary">Expires 24 hours after creation. Explicit external effects may persist.</Typography.Text> : null}
                          </Flex>
                          {modelSetup && <Alert type="info" showIcon className="new-chat-model-setup" title={modelSetup.title}
                            description={<Flex vertical align="start" gap="var(--ac-space-default)">
                              <Typography.Text>{modelSetup.description}</Typography.Text>
                              {modelSetup.href && <Button href={modelSetup.href} style={{ minHeight: isNarrow ? token.controlHeightLG : undefined }} onClick={event => followAppLink(event)}>{modelSetup.action}</Button>}
                            </Flex>} />}
                          {state.chatAgentInstances.length > 0 && !tenantCanCreateChat() && <Typography.Paragraph type="secondary">Ask an organization owner for permission to start a conversation.</Typography.Paragraph>}
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
                        {state.lifecycleStatus === "completed" && state.lifecycleSource === "agent" ? `Conversation completed by agent${state.lifecycleReason ? ` · ${state.lifecycleReason}` : ""}` : terminalSessionNote(state.lifecycleStatus)}
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
                      modelSupportsFileTools={selectedModel?.tools}
                      draft={state.draft}
                      parts={state.draftParts}
                      instanceId={inSession ? backgroundInstanceId : parseChatIdentityKey(state.newChatIdentityKey)?.instanceId}
                      onPartsChange={setDraftParts}
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
        {!expired && backgroundInstanceId && activitySource ? <BackgroundWorkDrawer source={activitySource} instanceId={backgroundInstanceId} open={workOpen} wide={!isNarrow}
          onClose={() => setWorkOpen(false)} afterClose={() => workButtonRef.current?.focus()} /> : null}
        {state.sessionId && !expired && workflowsOpen && <Suspense fallback={<Spin aria-label="Loading Workflow tools" />}><SessionWorkflowsDrawer context={tenantContext} session={state.sessionId} open narrow={isNarrow} openerRef={workflowsButtonRef} onClose={() => setWorkflowsOpen(false)} /></Suspense>}
        {state.sessionId && !expired && <TaskCenterDrawer key={`tasks:${tenantContext.tenant.tenantId}:${tenantContext.principalId}:${state.sessionId}`} tenant={tenantContext.tenant.tenantId} session={state.sessionId} open={tasksOpen} narrow={isNarrow} readonly={readonly} openerRef={tasksButtonRef} initialTaskId={requestedTaskId} onClose={() => setTasksOpen(false)} />}
        {state.sessionId && !expired && <SessionGoalsDrawer key={`goals:${tenantContext.tenant.tenantId}:${tenantContext.principalId}:${state.sessionId}:${requestedGoalId ?? ''}`} tenant={tenantContext.tenant.tenantId} session={state.sessionId} instance={backgroundInstanceId ?? ''} source={activitySource} open={goalsOpen} narrow={isNarrow} readonly={readonly} refreshKey={`${state.agentRunId ?? ''}:${state.liveResponseId ?? ''}`} openerRef={goalsButtonRef} onClose={() => setGoalsOpen(false)} onWorked={() => { void openCatalogSession({ sessionId: state.sessionId!, status: "created", archived: false, ended: false }); }} />}
        {state.sessionId && !expired && <TenantSessionFilesDrawer key={`files:${tenantContext.tenant.tenantId}:${state.sessionId}`} tenant={tenantContext.tenant.tenantId} organization={tenantContext.tenant.name} sessionId={state.sessionId} open={filesOpen} narrow={isNarrow} openerRef={filesButtonRef} onClose={() => setFilesOpen(false)} />}
        {state.sessionId && !expired && <TenantSessionSandboxDrawer key={`${tenantContext.tenant.tenantId}:${state.sessionId}`} tenant={tenantContext.tenant.tenantId} organization={tenantContext.tenant.name} sessionId={state.sessionId} open={sandboxOpen} openerRef={sandboxButtonRef} narrow={isNarrow} onClose={() => setSandboxOpen(false)} />}

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

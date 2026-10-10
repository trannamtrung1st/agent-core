import { adminHomePath, navigateToAppPath } from "../../app/appRoute";
import { AutomationEventFilter } from "./AutomationEventFilter";
import type { AutomationPreset, AutomationChild, EventCatalogEntry, EventSourceReference } from "../../services/adminApi";
import { EventsSection } from "./EventsSection";
import { AdminSessionPicker } from "./AdminSessionPicker";
import { AutomationDestination, ConversationDestination } from "../chat/AutomationDestination";
import { AdminErrorNotice } from "./adminFailure";
import { useCallback, useEffect, useId, useRef, useState, type Key } from "react";
import { Alert, App, Button, Checkbox, Collapse, DatePicker, Descriptions, Drawer, Empty, Flex, Form, Grid, Input, InputNumber, Select, Spin, Switch, Table, Tag, Typography, theme } from "antd";
import dayjs from "dayjs";
import { confirmAction } from "../../app/confirmAction";
import { listModels, type ModelDescriptor } from "../../services/api";
import { instanceContinuityRequest as request, type Automation, type AutomationDraft, type AutomationReview, type ScheduleTiming, listEventCatalog, eventSourceKey, type AdminWebhookEvent } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { ExecutionModelFields } from "./ExecutionModelFields";
import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";
import { useAdminDetailLayout } from "./useAdminDetailLayout";

import { runStatusLabel, runOutcomeLabel, type AutomationSelection } from "../chat/runPresentation";
import { useAutomationSelection } from "./useAutomationSelection";

const webhookExample = { schemaVersion: 1, type: "webhook.example", source: { kind: "webhook", key: "example" }, data: { status: "paid", total: 125 } };
const activeWork = ["Queued", "Running", "WaitingForApproval", "WaitingToRetry", "WaitingForSignal"];
const terminal = ["Completed", "Cancelled", "Expired"];
const date = (value: string | null) => value ? new Date(value).toLocaleString() : "Not scheduled";
const defaultTiming = (): ScheduleTiming => ({ kind: "daily", timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC", interval: 1, localTime: "09:00" });
const localZone = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
const supportedTimeZones = [...new Set([localZone, "UTC", ...Intl.supportedValuesOf("timeZone")])].sort();
const blank = (): AutomationDraft => ({ executionTarget: { kind: "backgroundSession" }, completionDelivery: { kind: "none" }, expectedRevision: 0, enabled: false, name: "", instructions: "", modelKey: null, reasoningEffort: null,
  triggers: [{ triggerId: crypto.randomUUID(), enabled: true, revision: 1, kind: "schedule", schedule: defaultTiming() }] });

export function InstanceAutomationsSection({ instanceId, onWork, selection, active = true }: { instanceId: string; active?: boolean; onWork: (workId?: string) => void; selection?: AutomationSelection }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const screens = Grid.useBreakpoint();
  const formId = useId();
  const editorOpener = useRef<HTMLElement | null>(null);
  const savedFocus = useRef<string | null>(null);
  const restoreEditorFocus = useRef(false);
  const [drawerVisible, setDrawerVisible] = useState(false);
  const nameInput = useRef<import("antd").InputRef>(null);
  const [editorError, setEditorError] = useState<AdminFailureNotice | null>(null);
  const newButton = useRef<HTMLButtonElement>(null);
  const [savedSelection, setSavedSelection] = useState<AutomationSelection>();
  useEffect(() => setSavedSelection(undefined), [selection]);
  const [expanded, setExpanded] = useState<Key[]>([]);
  const detailLayout = useAdminDetailLayout();
  const [review, setReview] = useState<AutomationReview | null>(null);
  const [managingEvents, setManagingEvents] = useState(false);
  const [sources, setSources] = useState<AdminWebhookEvent[]>([]);
  const [catalog, setCatalog] = useState<EventCatalogEntry[]>([]);
  const [savedSchedule, setSavedSchedule] = useState<AutomationChild[]>(blank().triggers);
  const [savedEvents, setSavedEvents] = useState<AutomationChild[]>([]);
  const [samples, setSamples] = useState<Record<string, string>>({});
  const [viewingSource, setViewingSource] = useState<EventSourceReference | null>(null);
  const [eventPickerOpen, setEventPickerOpen] = useState(false);
  const [expandedEvents, setExpandedEvents] = useState<string[]>([]);
  const managingTrigger = useRef<string | null>(null);
  const eventOpener = useRef<HTMLElement | null>(null);
  const [presets, setPresets] = useState<AutomationPreset[]>([]);
  const [deliveries, setDeliveries] = useState<{ eventId: string; automationId: string; triggerId: string; triggerRevision: number; status: string; code: string | null }[]>([]);
  const optionsGeneration = useRef(0);
  const deliveryGeneration = useRef(0);
  const [deliveryError, setDeliveryError] = useState(false);
  async function loadDeliveries() { const version = ++deliveryGeneration.current; try { const rows = await request<typeof deliveries>(instanceId, "automations/deliveries"); if (!Array.isArray(rows)) throw new Error("Invalid deliveries"); if (version !== deliveryGeneration.current) return; setDeliveries(rows); setDeliveryError(false); } catch { if (version === deliveryGeneration.current) setDeliveryError(true); } }
  const [optionsError, setOptionsError] = useState(false);
  const reloadOptions = useCallback(async () => {
    const version = ++optionsGeneration.current;
    try { const recipes = await request<AutomationPreset[]>(instanceId, "automations/presets"); if (!Array.isArray(recipes)) throw new Error("Invalid editor options"); if (version !== optionsGeneration.current) return; setPresets(recipes); setOptionsError(false); }
    catch { if (version === optionsGeneration.current) setOptionsError(true); }
  }, [instanceId]);
  useEffect(() => { setPresets([]); setDeliveries([]); void reloadOptions(); return () => { optionsGeneration.current++; deliveryGeneration.current++; }; }, [reloadOptions]);
  const [models, setModels] = useState<ModelDescriptor[]>([]);
  const [draft, setDraft] = useState<AutomationDraft>(blank);
  const [editor, setEditor] = useState<string | null>(null);
  const [editorOpen, setEditorOpen] = useState(false);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [resourcesError, setResourcesError] = useState<AdminFailureNotice | null>(null);
  const [catalogReady, setCatalogReady] = useState({ builtin: false, webhook: false });
  const resourcesGeneration = useRef(0);
  const reloadResources = useCallback(async () => {
    const generation = ++resourcesGeneration.current;
    setCatalogReady({ builtin: false, webhook: false });
    try {
      const results = await Promise.allSettled([listEventCatalog("builtin"), listEventCatalog("webhook"), listModels()]);
      if (generation !== resourcesGeneration.current) return;
      const builtin = results[0], webhook = results[1], models = results[2];
      setCatalogReady({ builtin: builtin.status === "fulfilled", webhook: webhook.status === "fulfilled" });
      setCatalog(current => [...(builtin.status === "fulfilled" ? builtin.value : current.filter(e => e.source.kind === "builtin")), ...(webhook.status === "fulfilled" ? webhook.value : current.filter(e => e.source.kind === "webhook"))]);
      if (webhook.status === "fulfilled") setSources(webhook.value.flatMap(e => e.webhook ? [e.webhook] : []));
      if (models.status === "fulfilled") setModels(models.value.models.filter(m => m.tools));
      const failed = results.find(r => r.status === "rejected");
      setResourcesError(failed?.status === "rejected" ? describeAdminError(failed.reason, "Some Events or models could not be refreshed. Loaded definitions and your draft are retained.") : null);
    } catch (reason) {
      if (generation === resourcesGeneration.current) setResourcesError(describeAdminError(reason, "Events and execution models could not be loaded. Retry to refresh them."));
    }
  }, []);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (!restoreEditorFocus.current || busy || drawerVisible || editor || !active) return;
    const source = savedFocus.current ? document.querySelector<HTMLButtonElement>(`[data-automation-id="${savedFocus.current}"]`) : null;
    (source ?? (editorOpener.current?.isConnected ? editorOpener.current : newButton.current))?.focus();
    savedFocus.current = null; restoreEditorFocus.current = false;
  }, [busy, drawerVisible, editor, active, review]);
  const [pending, setPending] = useState<Record<string, string | null>>({});
  const order = useRef({ generation: 0, mutating: false, pending: {} as Record<string, string | null> });
  const apply = useCallback((next: AutomationReview) => {
    order.current.pending = Object.fromEntries(Object.entries(order.current.pending).filter(([id, prior]) => {
      const item = next.items.find(i => i.automationId === id);
      return item && (!item.lastAgentRunId || item.lastAgentRunId === prior);
    }));
    setPending(order.current.pending); setReview(next);
  }, []);
  const reload = useCallback(async (initial = false) => {
    if (order.current.mutating) return;
    const generation = ++order.current.generation;
    if (initial) setLoading(true);
    try {
      const next = await request<AutomationReview>(instanceId, "automations");
      if (generation === order.current.generation) { apply(next); setError(null); }
    } catch (reason) { if (generation === order.current.generation) setError(describeAdminError(reason, "Automations could not be loaded. Reload to retry.")); }
    finally { if (generation === order.current.generation) setLoading(false); }
  }, [instanceId, apply]);
  useEffect(() => {
    void reload(true);
    void reloadResources();
    return () => { order.current.generation++; resourcesGeneration.current++; };
  }, [reload, reloadResources]);
  useEffect(() => {
    if (loading || !active) return;
    const timer = window.setInterval(() => { void reload(); }, 5000); return () => window.clearInterval(timer);
  }, [loading, reload, active]);
  const previousActive = useRef(active);
  useEffect(() => {
    if (active && !previousActive.current) { void reload(); void reloadResources(); }
    previousActive.current = active;
  }, [active, reload, reloadResources]);
  useEffect(() => {
    if (selection?.kind === "automation") void reload(true);
  }, [selection, reload]);
  async function mutate(path: string, body: unknown, method = "POST", saveEditor = false) {
    const runId = /^automations\/([^/]+)\/run$/.exec(path)?.[1];
    if (order.current.mutating || runId && runId in order.current.pending) return;
    order.current.mutating = true; ++order.current.generation; setBusy(true); setError(null);
    if (saveEditor) setEditorError(null);
    if (runId) { order.current.pending = { ...order.current.pending, [runId]: review?.items.find(i => i.automationId === runId)?.lastAgentRunId ?? null }; setPending(order.current.pending); }
    let accepted = false;
    let savedId: string | undefined;
    try {
      const saved = await request<Automation>(instanceId, path, method, body); accepted = true; savedId = saved?.automationId;
      if (savedId && !saveEditor && method === "PUT") setSavedSelection({ kind: "automation", automationId: saved.automationId, request: Date.now() });
      if (method === "DELETE") { setExpanded([]); setSavedSelection(undefined); }
      if (saveEditor) savedFocus.current = saved?.automationId ?? null;
      apply(await request<AutomationReview>(instanceId, "automations"));
    } catch (reason) {
      if (runId && !accepted) { const next = { ...order.current.pending }; delete next[runId]; order.current.pending = next; setPending(next); }
      const failure = describeAdminError(reason, "Automation update failed. Reload for the current revision.");
      if (saveEditor && !accepted) setEditorError(failure); else setError(failure);
    } finally {
      if (saveEditor && accepted) {
        if (savedId) setSavedSelection({ kind: "automation", automationId: savedId, request: Date.now() });
        setEditorOpen(false);
      }
      order.current.mutating = false; setBusy(false);
    }
  }
  const tableVersion = useAutomationSelection(savedSelection ?? selection, "automation", review?.items.map(item => item.automationId) ?? [], setSearch, setExpanded);
  const isSchedule = draft.triggers[0]?.kind === "schedule";
  const timing = draft.triggers[0]?.kind === "schedule" ? draft.triggers[0].schedule : defaultTiming();
  const viewerZone = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  const previewTime = (value?: string | null) => value && Number.isFinite(Date.parse(value))
    ? `${new Date(value).toLocaleString()} (${viewerZone})` : null;
  const policy = review?.policy;
  const coreAllowed = policy?.allowCoreEvents === true;
  const eventsAllowed = policy?.allowEvents === true;
    const schedulesAllowed = !!policy && (policy.allowOneShot || policy.allowDaily || policy.allowWeekly || policy.allowFixedInterval);
  const kindAllowed = !!policy && ({ oneShot: policy.allowOneShot, daily: policy.allowDaily, weekly: policy.allowWeekly, fixedInterval: policy.allowFixedInterval })[timing.kind];
  const hasEnd = !!timing.maxOccurrences || (timing.kind === "fixedInterval" ? !!timing.endAtUtc : !!timing.endDate);
  const maximum = timing.kind === "fixedInterval" ? 604800 : timing.kind === "daily" ? 365 : 52;
  const scheduleValid =
    (timing.maxOccurrences == null || Number.isInteger(timing.maxOccurrences) && timing.maxOccurrences >= 1 && timing.maxOccurrences <= 366) && draft.instructions.trim().length > 0 && draft.instructions.trim().length <= 2000 &&
    (timing.kind === "oneShot" ? !!timing.atUtc && Number.isFinite(Date.parse(timing.atUtc)) && Date.parse(timing.atUtc) > Date.now() :
      timing.interval >= (timing.kind === "fixedInterval" ? 60 : 1) && timing.interval <= maximum && (timing.kind === "fixedInterval" ||
        /^\d{2}:\d{2}$/.test(timing.localTime ?? "") && !!timing.timeZone.trim() && (timing.kind !== "weekly" || !!timing.weekdays?.length)));
  const destinationValid = (draft.executionTarget.kind !== "existingSession" || !!draft.executionTarget.sessionId)
    && (draft.completionDelivery.kind !== "toSession" || !!draft.completionDelivery.sessionId);
  const valid = destinationValid && draft.name.trim().length > 0 && draft.name.trim().length <= 120 && draft.instructions.trim().length > 0 && draft.instructions.trim().length <= 2000
    && (isSchedule ? scheduleValid : draft.triggers.length > 0 && draft.triggers.every(t => !!t.source));
  const eligible = isSchedule ? schedulesAllowed && kindAllowed && (timing.kind === "oneShot" || policy?.allowIndefiniteRecurrence !== false || hasEnd)
    : draft.triggers.some(t => t.enabled && t.source && (t.source.kind === "builtin" ? coreAllowed : eventsAllowed && sources.some(e => e.eventId === (t.source?.kind === "webhook" ? t.source.eventId : "") && e.status === "Active")));
  const authorityReady = !!review && !error && !loading;
  const sourcesReady = isSchedule || draft.triggers.every(t => !t.enabled || t.source && catalogReady[t.source.kind]
    && catalog.some(e => eventSourceKey(e.source) === eventSourceKey(t.source!)));
  const canEnable = eligible && authorityReady && sourcesReady;
  const canSave = valid && (!draft.enabled || canEnable);
  function setTiming(change: Partial<ScheduleTiming>) { setDraft({ ...draft, triggers: [{ triggerId: draft.triggers[0]!.triggerId, enabled: draft.triggers[0]!.enabled, revision: draft.triggers[0]!.revision, kind: "schedule", schedule: { ...timing, ...change } }] }); }
  function resetDraft(next: AutomationDraft) { setSamples({}); setDraft(next); setSavedSchedule(next.triggers[0]?.kind === "schedule" ? next.triggers : blank().triggers); setSavedEvents(next.triggers[0]?.kind === "schedule" ? [] : next.triggers); setExpandedEvents(next.triggers[0]?.kind === "schedule" ? [] : next.triggers.slice(0, 1).map(t => t.triggerId)); }
  function switchMode(kind: string) {
    if (kind === "schedule") { setSavedEvents(draft.triggers); setDraft({ ...draft, triggers: savedSchedule }); }
    else { setSavedSchedule(draft.triggers); setDraft({ ...draft, triggers: savedEvents }); }
  }
  function edit(item: Automation) { editorOpener.current = document.activeElement as HTMLElement; setEditorError(null); setEditor(item.automationId); setEditorOpen(true); resetDraft({ expectedRevision: item.revision, enabled: item.enabled, name: item.name, instructions: item.instructions,
    presetId: item.presetId, presetVersion: item.presetVersion, triggers: item.triggers, modelKey: item.modelKey, reasoningEffort: item.reasoningEffort, executionTarget: item.executionTarget, completionDelivery: item.completionDelivery, requiresTools: item.requiresTools, requiresVision: item.requiresVision }); }
  function childName(child: AutomationChild) { return child.source ? catalog.find(e => eventSourceKey(e.source) === eventSourceKey(child.source!))?.name ?? (child.source.kind === "builtin" ? child.source.key : "Webhook unavailable") : "Choose an Event"; }
  function updateChild(triggerId: string, change: Partial<AutomationChild>) { setDraft(current => ({ ...current, triggers: current.triggers.map(t => t.triggerId === triggerId ? { ...t, ...change } as AutomationChild : t) })); }
  function addEvent(source: EventSourceReference) {
    const triggerId = crypto.randomUUID();
    setDraft(current => ({ ...current, triggers: [...current.triggers, { triggerId, enabled: true, revision: 1, kind: "event", source }] }));
    setExpandedEvents([triggerId]); setEventPickerOpen(false);
  }
  async function saveDraft(enabled = draft.enabled) {
    if (!editorOpen || !valid || busy || (enabled && !canEnable)) return;
    const triggers = draft.triggers.map(t => t.kind === "schedule" ? { ...t, schedule: timing.kind === "fixedInterval" && !timing.anchorAtUtc ? { ...timing, anchorAtUtc: new Date(Date.now() + timing.interval * 1000).toISOString() } : timing }
      : { triggerId: t.triggerId, enabled: t.enabled, revision: t.revision, kind: "event", source: t.source, filterExpression: t.filterExpression, dispatch: t.dispatch });
    void mutate(editor === "new" ? "automations" : `automations/${editor}`, { ...draft, enabled, triggers }, editor === "new" ? "POST" : "PUT", true);
  }
  const summaryTrigger = isSchedule ? timing.kind === "oneShot" && !timing.atUtc ? "Choose when to run once" : scheduleTimingLabel(timing) : draft.triggers.length ? `${draft.triggers.length} Event subscription(s) · any matching Event can run` : "Add an Event";
  const summaryBounds = !isSchedule || timing.kind === "oneShot" ? "" : [
    timing.kind === "fixedInterval" ? timing.endAtUtc ? `Ends ${date(timing.endAtUtc)}` : null : timing.endDate ? `Ends ${timing.endDate}` : null,
    timing.maxOccurrences ? `Up to ${timing.maxOccurrences} occurrences` : null
  ].filter(Boolean).join(" · ");
  return <section className="admin-definition-panel" aria-label="Automations">
    <div className="admin-definition-panel-heading"><Typography.Title level={4}>Automations</Typography.Title>
      <Typography.Text type="secondary">Choose when the agent follows your instructions. Each Run uses its authorized capabilities and normal approvals.</Typography.Text></div>
    <div className="admin-definition-panel-body"><Flex vertical gap={token.padding}>
      {loading ? <Spin aria-label="Loading automations" /> : null}
      {selection?.kind === "automation" && !loading && !error && review && !review.items.some(item => item.automationId === selection.automationId) ? <Alert type="info" showIcon title="This source configuration is no longer available" description="It may have been deleted or retired. Its run remains available in Runs." /> : null}
      {error ? <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button disabled={busy} onClick={() => void reload()}>Reload automations</Button>} /> : null}
      {resourcesError ? <Alert type="error" showIcon title={<AdminErrorNotice message={resourcesError.message} diagnosticId={resourcesError.diagnosticId} showDetailsLabel />} action={<Button disabled={busy} onClick={() => void reloadResources()}>Reload Events and models</Button>} /> : null}
      <Flex wrap gap={token.paddingXS}><Button type="primary" ref={newButton} disabled={busy} onClick={() => { editorOpener.current = newButton.current; setEditorError(null); resetDraft(blank()); setEditor("new"); setEditorOpen(true); }}>New automation</Button>
        <Button disabled={busy} onClick={() => void reload()}>Refresh automations</Button><Button onClick={() => onWork()}>View runs</Button></Flex>
      <Drawer open={editorOpen && active} title={editor === "new" ? "New automation" : "Edit automation"}
        size={screens.md ? 640 : "100%"} getContainer={false} rootStyle={{ position: "fixed" }}
        rootClassName="admin-automation-drawer" styles={{ body: { padding: token.padding }, footer: { padding: token.padding } }}
        focusable={{ trap: editorOpen && active && !managingEvents, focusTriggerAfterClose: false }}
        closable={!busy} maskClosable={!busy} keyboard={!busy} onClose={() => setEditorOpen(false)}
        afterOpenChange={open => {
          // Keep the last form and mode intact until the exit animation finishes.
          // Hiding an instance tab must still retain its unsaved editor.
          if (!open && !editorOpen) {
            setEditor(null); setDraft(blank());
            if (active) restoreEditorFocus.current = true;
          }
          setDrawerVisible(open);
          if (open) {
            const focused = document.activeElement;
            // Do not interrupt a control used while the drawer was animating in.
            if (focused === document.body || focused?.classList.contains("ant-drawer") || focused?.getAttribute("role") === "dialog") nameInput.current?.focus();
          }
        }}
        footer={<Flex vertical gap={token.paddingSM}>
          <Flex component="section" vertical gap={token.paddingXS} aria-label="Automation summary">
            <Typography.Text strong>Automation summary</Typography.Text>
            <Typography.Text>{summaryTrigger}{summaryBounds ? ` · ${summaryBounds}` : ""}</Typography.Text>
            <Typography.Text>{draft.executionTarget.kind === "backgroundSession" ? "Run this agent in a background Session" : draft.executionTarget.sessionId ? `Run in conversation ${draft.executionTarget.sessionId}` : "Choose the conversation to run in"}</Typography.Text>
            <Typography.Text type="secondary">{draft.executionTarget.kind === "existingSession" ? "Reply directly in that conversation using its pinned model" : draft.completionDelivery.kind === "toSession" ? draft.completionDelivery.sessionId ? `Request a completion report to conversation ${draft.completionDelivery.sessionId}` : "Choose a conversation for the completion report" : "Keep results in Background work without a conversation report"}</Typography.Text>
            {draft.enabled && !canEnable ? <Typography.Text type="warning">{!eligible ? "No enabled subscription is currently eligible. Repair the source policy or save as disabled." : "Current authorization or Event sources could not be validated. Retry the failed read or save as disabled."}</Typography.Text> : null}
            {!draft.enabled ? <Typography.Text type="warning">Saved disabled. Enable this Automation before it can run.</Typography.Text> : null}
          </Flex>
          <Flex wrap justify="flex-end" gap={token.paddingXS}>
          <Button aria-label="Cancel automation edit" disabled={!editorOpen || busy} onClick={() => setEditorOpen(false)}>Cancel</Button>
          {draft.enabled && !canEnable ? <Button disabled={!valid || busy} onClick={() => void saveDraft(false)}>Save as disabled</Button> : null}
          <Button type="primary" aria-label={editor === "new" ? "Create automation" : "Save automation"} htmlType="submit" form={formId} disabled={!editorOpen || !canSave || busy} loading={busy}>{draft.enabled ? "Save & enable" : "Save"}</Button>
          </Flex>
        </Flex>}>
        {editorError ? <Alert style={{ marginBlockEnd: token.padding }} type="error" showIcon
          title={<AdminErrorNotice message={editorError.message} diagnosticId={editorError.diagnosticId} showDetailsLabel />}
          action={<Button disabled={busy} onClick={() => void reload()}>Reload automations</Button>} /> : null}
        {editor ? <Form id={formId} layout="vertical" className="admin-config-form automation-editor-form" onKeyDown={event => {
        if (event.key === "Enter" && (event.target as HTMLElement).closest(".ant-picker")) event.preventDefault();
      }} onFinish={() => void saveDraft()}>
        <Flex vertical gap={token.padding}>
        <Flex component="section" vertical gap={token.padding} className="automation-editor-section" aria-label="General">
        <Typography.Title level={5} style={{ margin: 0 }}>General</Typography.Title>
        {editor === "new" ? <>
          <Form.Item label="Start from"><Select aria-label="Automation preset" value={draft.presetId ?? "custom"} disabled={busy}
            options={[{ value: "custom", label: "Custom automation" }, ...presets.map(p => ({ value: p.presetId, label: p.name }))]}
            onChange={presetId => { const p = presets.find(p => p.presetId === presetId); resetDraft(p ? { ...blank(), enabled: false, name: p.name, instructions: p.instructions, triggers: [{ ...p.trigger, triggerId: crypto.randomUUID(), enabled: true, revision: 1, ...(p.trigger.kind === "coreEvent" ? { kind: "event", source: { kind: "builtin", key: p.trigger.coreEventKey } } : {}) } as AutomationChild], presetId: p.presetId, presetVersion: p.presetVersion, requiresTools: true } : blank()); }} /></Form.Item>
          {optionsError ? <Alert type="warning" showIcon title="Presets could not be loaded" action={<Button onClick={() => void reloadOptions()}>Retry options</Button>} /> : null}
          {presets.find(p => p.presetId === draft.presetId)?.prerequisites.map(reason => <Alert key={reason} type="info" showIcon title={reason} />)}
        </> : null}
        <Form.Item label="Name"><Input ref={nameInput} aria-label="Automation name" maxLength={120} value={draft.name} disabled={busy} onChange={e => setDraft({ ...draft, name: e.target.value })} /></Form.Item>
        <Form.Item label="Instructions" extra={`${draft.instructions.length} / 2000 characters`}><Input.TextArea aria-label="Automation instructions" rows={4} maxLength={2000} value={draft.instructions} disabled={busy} onChange={e => setDraft({ ...draft, instructions: e.target.value })} /></Form.Item>
        <Form.Item label="Enabled" layout="horizontal" colon={false} labelCol={{ flex: "none" }} wrapperCol={{ flex: "none" }}><Switch aria-label="Enable automation" checked={draft.enabled} disabled={busy} onChange={enabled => setDraft({ ...draft, enabled })} /></Form.Item>
        </Flex>
        <Flex component="section" vertical gap={token.padding} className="automation-editor-section" aria-label="Trigger">
        <Typography.Title level={5} style={{ margin: 0 }}>Trigger</Typography.Title>
        {policy ? <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Up to {policy.maxActiveRegistrations} active Automations. One-time schedules must be within {policy.oneShotHorizonDays} days; fixed intervals must be at least {policy.minFixedIntervalSeconds} seconds.</Typography.Paragraph> : null}
        <Form.Item label="When"><Select aria-label="Automation trigger" value={isSchedule ? "schedule" : "events"} disabled={busy}
          options={[{ value: "schedule", label: "Schedule" }, { value: "events", label: "Events" }]} onChange={switchMode} /></Form.Item>
        {!isSchedule ? <>
          <Typography.Text type="secondary">Each Event matches independently. Grouping applies only within its subscription.</Typography.Text>
          {resourcesError ? <Alert type="error" showIcon title="Event catalog unavailable. Your draft is retained." action={<Button onClick={() => void reloadResources()}>Retry catalog</Button>} /> : null}
          <Collapse activeKey={expandedEvents} onChange={keys => setExpandedEvents(keys as string[])} items={draft.triggers.map((child, index) => ({
            key: child.triggerId, forceRender: true, label: <Flex gap={token.paddingXS} align="center" wrap><Typography.Text strong>{childName(child)}{child.source?.kind === "webhook" ? ` · ${catalog.find(e => eventSourceKey(e.source) === eventSourceKey(child.source!))?.key ?? child.source.eventId}` : ""}</Typography.Text><Tag>{child.source?.kind === "builtin" ? "Built-in" : "Webhook"}</Tag><Typography.Text type="secondary">{child.enabled ? "Enabled" : "Disabled"}{child.eligible === false ? " · Unavailable" : ""} · {child.kind !== "schedule" && child.dispatch?.mode === "coalesceLatest" ? `Grouped ${child.dispatch.windowSeconds}s` : "Every match"}</Typography.Text></Flex>,
            children: <Flex vertical gap={token.paddingSM} role="region" aria-label={`Event subscription ${index + 1}`}>
              <Flex wrap gap={token.paddingXS} align="center"><Switch aria-label={`Enable ${childName(child)} trigger`} checked={child.enabled} disabled={busy} onChange={enabled => updateChild(child.triggerId, { enabled })} /><Typography.Text>Subscription enabled</Typography.Text>
                <Button size="small" onClick={e => { managingTrigger.current = child.triggerId; eventOpener.current = e.currentTarget; setViewingSource(child.source ?? null); setManagingEvents(true); }}>View definition</Button>
                <Button danger size="small" disabled={busy} onClick={() => confirmAction(modal, { title: "Remove this Event trigger?", content: "Stops future admissions after saving. Existing Runs and source evidence remain available.", okText: "Remove trigger", danger: true,
                  onOk: () => { setDraft(current => ({ ...current, triggers: current.triggers.filter(t => t.triggerId !== child.triggerId) })); setExpandedEvents(keys => keys.filter(k => k !== child.triggerId)); } })}>Remove trigger</Button>
              </Flex>
              {child.source && !(child.source.kind === "builtin" ? coreAllowed : eventsAllowed && sources.some(e => e.eventId === (child.source?.kind === "webhook" ? child.source.eventId : "") && e.status === "Active")) ? <Alert type="warning" showIcon title={child.source.kind === "builtin" ? "Allow Built-in Events in the active Definition to run this subscription." : "Allow Webhook Events in the Definition and activate this Event to run this subscription."} /> : null}
              {child.eligibilityReason ? <Typography.Text type="warning">{child.eligibilityReason}</Typography.Text> : null}
              {child.kind !== "schedule" ? <AutomationEventFilter instanceId={instanceId} trigger={child} sampleValue={samples[child.triggerId]} onSampleChange={sample => setSamples(current => ({ ...current, [child.triggerId]: sample }))} example={catalog.find(e => child.source && eventSourceKey(e.source) === eventSourceKey(child.source))?.example ?? webhookExample} disabled={busy} onChange={trigger => updateChild(child.triggerId, trigger)} /> : null}
              <Button size="small" style={{ alignSelf: "flex-start" }} onClick={e => { managingTrigger.current = child.triggerId; eventOpener.current = e.currentTarget; setViewingSource(null); setManagingEvents(true); }}>Create or manage Events</Button>
            </Flex>
          }))} />
          <Button style={{ alignSelf: "flex-start" }} disabled={busy || catalog.length === 0 || draft.triggers.length >= 32} onClick={e => { eventOpener.current = e.currentTarget; setEventPickerOpen(true); }}>Add Event</Button>
          <Typography.Text type="secondary">Up to 32 subscriptions within the 64 KiB configuration budget. Duplicate sources are not allowed.</Typography.Text>
          {eventPickerOpen ? <Form.Item label="Add Event"><Select autoFocus open aria-label="Add Event source" showSearch optionFilterProp="label" value={undefined} placeholder="Search Built-in and Webhook Events"
            options={["builtin", "webhook"].map(kind => ({ label: kind === "builtin" ? "Built-in" : "Webhook", options: catalog.filter(e => e.source.kind === kind).map(e => ({ value: eventSourceKey(e.source), label: `${e.name} · ${e.key}${draft.triggers.some(t => t.source && eventSourceKey(t.source) === eventSourceKey(e.source)) ? " · Already added" : ""}`, disabled: draft.triggers.some(t => t.source && eventSourceKey(t.source) === eventSourceKey(e.source)) })) }))}
            onSelect={key => { const e = catalog.find(e => eventSourceKey(e.source) === key); if (e) addEvent(e.source); }} onOpenChange={open => { if (!open) { setEventPickerOpen(false); eventOpener.current?.focus(); } }} /></Form.Item> : null}
          <Button size="small" style={{ alignSelf: "flex-start" }} onClick={e => { managingTrigger.current = null; eventOpener.current = e.currentTarget; setViewingSource(null); setManagingEvents(true); }}>New webhook Event</Button>
        </> : <>
        {!schedulesAllowed ? <Alert showIcon type="warning" title="This Definition does not permit Schedule Automations." /> : null}
        <div className="automation-timing-fields">
          <Form.Item label="Timing"><Select aria-label="Schedule timing" value={timing.kind} disabled={busy}
            options={[{ value: "oneShot", label: "Once",  }, { value: "daily", label: "Daily",  }, { value: "weekly", label: "Weekly",  }, { value: "fixedInterval", label: "Fixed interval",  }]}
            onChange={kind => setTiming({ kind, timeZone: timing.timeZone, interval: kind === "fixedInterval" ? 3600 : 1, localTime: "09:00", weekdays: kind === "weekly" ? [1] : null })} /></Form.Item>
          {timing.kind === "oneShot" ? <Form.Item className="automation-timing-wide" label="Run at" extra={`Times shown in ${viewerZone}. Saved in UTC.`}>
            <DatePicker aria-label="Schedule run at" classNames={{ popup: { root: "automation-date-picker-popup" } }} showTime={{ format: "HH:mm" }} format="YYYY-MM-DD HH:mm"
              value={timing.atUtc ? dayjs(timing.atUtc) : null} disabled={busy}
              onChange={value => setTiming({ atUtc: value?.toISOString() ?? null })} />
          </Form.Item> :
            <Form.Item label={timing.kind === "fixedInterval" ? "Interval (seconds)" : timing.kind === "daily" ? "Every (days)" : "Every (weeks)"}>
              <InputNumber aria-label={timing.kind === "fixedInterval" ? "Schedule interval (seconds)" : timing.kind === "daily" ? "Schedule every (days)" : "Schedule every (weeks)"} min={timing.kind === "fixedInterval" ? 60 : 1} max={maximum} precision={0} value={timing.interval} disabled={busy} onChange={value => setTiming({ interval: value ?? 0 })} /></Form.Item>}
          {timing.kind === "daily" || timing.kind === "weekly" ? <><Form.Item label="Local time"><Input aria-label="Schedule local time" type="time" value={timing.localTime ?? "09:00"} disabled={busy} onChange={e => setTiming({ localTime: e.target.value })} /></Form.Item>
            <Form.Item className="automation-timing-zone" label="Time zone" extra="Runs at the selected local time. Daylight saving changes follow this zone; missing times move forward and repeated times run once."><Select aria-label="Schedule time zone" showSearch optionFilterProp="label" value={timing.timeZone} disabled={busy} options={[...new Set([timing.timeZone, ...supportedTimeZones])].filter((value): value is string => !!value).sort().map(value => ({ value, label: value }))} onChange={timeZone => setTiming({ timeZone })} /></Form.Item></> : null}
        </div>
        {timing.kind === "weekly" ? <Form.Item label="Weekdays"><Select mode="multiple" aria-label="Schedule weekdays" value={timing.weekdays ?? []} disabled={busy}
          options={["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"].map((label, value) => ({ label, value }))} onChange={weekdays => setTiming({ weekdays })} /></Form.Item> : null}
        {timing.kind !== "oneShot" ? <>
          {policy?.allowIndefiniteRecurrence === false ? <Alert type="info" showIcon title="This Definition requires an end date or occurrence limit." /> : null}
          <div className="automation-limit-fields">
            <Form.Item label={timing.kind === "fixedInterval" ? "End at" : "End date"}
              extra={timing.kind === "fixedInterval" ? `Times shown in ${viewerZone}. Saved in UTC.` : undefined}>
              <DatePicker aria-label={timing.kind === "fixedInterval" ? "Schedule end at" : "Schedule end date"} classNames={{ popup: { root: "automation-date-picker-popup" } }}
                showTime={timing.kind === "fixedInterval" ? { format: "HH:mm" } : false}
                format={timing.kind === "fixedInterval" ? "YYYY-MM-DD HH:mm" : "YYYY-MM-DD"}
                value={timing.kind === "fixedInterval" ? timing.endAtUtc ? dayjs(timing.endAtUtc) : null : timing.endDate ? dayjs(timing.endDate) : null}
                disabled={busy} onChange={value => setTiming(timing.kind === "fixedInterval"
                  ? { endAtUtc: value?.toISOString() ?? null } : { endDate: value?.format("YYYY-MM-DD") ?? null })} />
            </Form.Item>
            <Form.Item label="Maximum occurrences">
              <InputNumber aria-label="Schedule maximum occurrences" min={1} precision={0} value={timing.maxOccurrences} disabled={busy} onInput={text => setTiming({ maxOccurrences: text.trim() ? Number(text) : null })} onChange={value => setTiming({ maxOccurrences: value })} />
            </Form.Item>
          </div>
          <Typography.Text type="secondary">Leave both bounds empty only if the Definition permits ongoing recurrence.</Typography.Text>
        </> : null}
        {timing.kind === "oneShot" && previewTime(timing.atUtc) ? <Typography.Paragraph style={{ margin: 0 }} type="secondary" role="status">
          Runs on {previewTime(timing.atUtc)}.
        </Typography.Paragraph> : null}
        {timing.kind === "fixedInterval" && previewTime(timing.endAtUtc) ? <Typography.Paragraph style={{ margin: 0 }} type="secondary" role="status">
          Stops on {previewTime(timing.endAtUtc)}.
        </Typography.Paragraph> : null}
        </>}
        </Flex>
        <Flex component="section" vertical gap={token.padding} className="automation-editor-section" aria-label="Execution">
        <Typography.Title level={5} style={{ margin: 0 }}>Execution</Typography.Title>
        <Form.Item label="Run in" extra={draft.executionTarget.kind === "existingSession" ? "Replies in this conversation, even when you are away. Uses its pinned model." : "Runs independently. Results stay in Background work unless you request a report."}>
          <Select aria-label="Automation destination" disabled={busy} value={draft.executionTarget.kind}
            options={[{ value: "backgroundSession", label: "Separate background Session" }, { value: "existingSession", label: "Selected conversation" }]}
            onChange={kind => setDraft({ ...draft, executionTarget: { kind }, completionDelivery: { kind: "none" }, modelKey: null, reasoningEffort: null })} />
        </Form.Item>
        {draft.executionTarget.kind === "existingSession" ? <Form.Item label="Destination conversation">
          <AdminSessionPicker instanceId={instanceId} value={draft.executionTarget.sessionId ?? ""} disabled={busy} eligibleOnly label="Destination conversation"
            onChange={sessionId => setDraft({ ...draft, executionTarget: { kind: "existingSession", sessionId } })} />
        </Form.Item> : null}
        </Flex>
        {draft.executionTarget.kind === "backgroundSession" ? <Flex component="section" vertical gap={token.padding} className="automation-editor-section" aria-label="Completion">
          <Typography.Title level={5} style={{ margin: 0 }}>Completion</Typography.Title>
          <Form.Item label="Report completion to"><Select aria-label="Automation completion report" disabled={busy} value={draft.completionDelivery.kind}
            options={[{ value: "none", label: "None" }, { value: "toSession", label: "Selected conversation" }]}
            onChange={kind => setDraft({ ...draft, completionDelivery: { kind } })} /></Form.Item>
          {draft.completionDelivery.kind === "toSession" ? <Form.Item label="Report destination">
            <AdminSessionPicker instanceId={instanceId} value={draft.completionDelivery.sessionId ?? ""} disabled={busy} eligibleOnly label="Report destination"
              onChange={sessionId => setDraft({ ...draft, completionDelivery: { kind: "toSession", sessionId } })} />
          </Form.Item> : null}
        </Flex> : null}
        <Collapse ghost className="automation-editor-section" styles={{ root: { borderRadius: 0 }, header: { padding: `${token.paddingXS}px 0`, minHeight: token.controlHeightLG }, body: { padding: `${token.padding}px 0 0` } }} items={[{ key: "advanced", label: "Advanced", forceRender: true, children: <Flex vertical gap={token.padding}>
        {draft.executionTarget.kind === "backgroundSession" ? <Form.Item label="Execution model" extra="Uses the instance unattended default unless you select a model. Each admitted run keeps its model."><ExecutionModelFields models={models}
          modelKey={draft.modelKey ?? ""} reasoningEffort={draft.reasoningEffort ?? ""} disabled={busy} modelLabel="Automation execution model" effortLabel="Automation reasoning effort" defaultLabel="Unattended default"
          onChange={(modelKey, reasoningEffort) => setDraft({ ...draft, modelKey: modelKey || null, reasoningEffort: reasoningEffort || null })} /></Form.Item> : null}
        <Flex wrap gap={token.padding}>
          <Checkbox checked={draft.requiresTools ?? false} disabled={busy} onChange={e => setDraft({ ...draft, requiresTools: e.target.checked })}>Task requires tools</Checkbox>
          <Checkbox checked={draft.requiresVision ?? false} disabled={busy} onChange={e => setDraft({ ...draft, requiresVision: e.target.checked })}>Task requires vision</Checkbox>
        </Flex>
        </Flex> }]} />
        </Flex>
      </Form> : null}
      </Drawer>
      <Drawer open={managingEvents && active} title="Global Events" size={screens.md ? 640 : "100%"} getContainer={false} rootStyle={{ position: "fixed" }}
        rootClassName="admin-automation-drawer" styles={{ body: { padding: token.padding } }} onClose={() => setManagingEvents(false)}
        afterOpenChange={open => { if (!open) (eventOpener.current?.isConnected ? eventOpener.current : nameInput.current?.input)?.focus(); }}>
        <EventsSection embedded instanceId={instanceId} initialSource={viewingSource} onChanged={events => { setSources(events); void reloadResources(); }} onCreated={eventId => { const source: EventSourceReference = { kind: "webhook", eventId }; if (managingTrigger.current) { const triggerId = crypto.randomUUID(); setDraft(current => ({ ...current, triggers: current.triggers.map(t => t.triggerId === managingTrigger.current ? { ...t, triggerId, revision: 1, kind: "event", source } : t) })); setExpandedEvents([triggerId]); managingTrigger.current = triggerId; } else addEvent(source); }} />
      </Drawer>
      <Collapse onChange={keys => { if (keys.length) void loadDeliveries(); }} items={[{ key: "deliveries", label: "Built-in Event deliveries", children: <Flex vertical gap={token.paddingSM}>
        {deliveryError ? <Alert type="error" showIcon title="Event deliveries could not be loaded" action={<Button onClick={() => void loadDeliveries()}>Retry deliveries</Button>} /> : null}
        <Table rowKey={row => `${row.eventId}:${row.triggerId}`} size="small" scroll={{ x: 650 }} dataSource={deliveries}
          columns={[{ title: "Automation", dataIndex: "automationId" }, { title: "Subscription", dataIndex: "triggerId" }, { title: "Revision", dataIndex: "triggerRevision" }, { title: "Outcome", dataIndex: "status" }, { title: "Reason", dataIndex: "code" }]}
          locale={{ emptyText: "No Built-in Event deliveries yet" }} />
      </Flex> }]} />
      {review ? review.items.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No automations yet. Create one here or ask the agent in Chat to do something later." /> :
        <>
        <AdminCollectionToolbar label="automations" value={search} onChange={setSearch} />
        <Table<Automation> key={tableVersion} aria-label="Automations table" className="admin-collection-table" size="small" rowKey="automationId"
          dataSource={review.items.filter(item => [item.name, item.instructions, item.status, automationWhen(item, sources),
            item.authorizationOrigin === "CurrentUserTurn" ? "Chat user request" : "Admin owner", item.sourceSessionId ?? "",
            item.effectiveModelKey ?? item.modelKey ?? "Unattended default", item.executionStatus ?? ""]
            .some(value => value.toLowerCase().includes(search.trim().toLowerCase()))).sort((a, b) => Number(b.automationId === selection?.automationId) - Number(a.automationId === selection?.automationId))}
          scroll={{ x: 1770 }} pagination={pagination}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No matches. Clear search or filters to see all results." /> }}
          columns={[
            { title: "Name", key: "task", width: 350, ellipsis: true,
              render: (_, item) => <Button type="link" size="small" className="admin-collection-name" title={item.name}
                data-automation-id={item.automationId} aria-label={`View automation: ${item.name}`} aria-expanded={expanded.includes(item.automationId)}
                onClick={() => setExpanded(expanded.includes(item.automationId) ? [] : [item.automationId])}>{item.name}</Button> },
            { title: "Destination", key: "destination", width: 240, render: (_, item) => <Flex vertical gap={token.paddingXS}><AutomationDestination target={item.executionTarget} delivery={item.completionDelivery} /></Flex> },
            { title: "When", key: "timing", width: 240, ellipsis: true, render: (_, item) => <span title={automationWhen(item, sources)}>{automationWhen(item, sources)}</span> },
            { title: "Status", dataIndex: "status", width: 120,
              filters: [...new Set(review.items.map(item => item.status))].map(value => ({ text: value, value })),
              onFilter: (value, item) => item.status === value, render: (status: string) => <Tag>{status}</Tag> },
            { title: "Next run", key: "next", width: 220,
              sorter: (a, b) => (a.enabled && !terminal.includes(a.status) ? a.nextRunAt ?? "" : "").localeCompare(b.enabled && !terminal.includes(b.status) ? b.nextRunAt ?? "" : ""),
              render: (_, item) => item.enabled && !terminal.includes(item.status) && item.triggers[0]?.kind !== "schedule" ? "On event" : date(item.enabled && !terminal.includes(item.status) ? item.nextRunAt : null) },
            { title: "Last run", key: "execution", width: 240,
              filters: [...new Set(review.items.map(item => item.executionStatus ?? "Not yet"))].map(value => ({ text: runStatusLabel(value), value })),
              onFilter: (value, item) => (item.executionStatus ?? "Not yet") === value,
              render: (_, item) => item.lastAgentRunId ? <Button type="link" size="small" aria-label={`View last run: ${item.name}`} onClick={() => onWork(item.lastAgentRunId!)}>{runStatusLabel(item.executionStatus ?? "View run")}{item.outcome ? ` · ${runOutcomeLabel(item.outcome)}` : ""}</Button> : "Not yet" },
            { title: "Model", key: "model", width: 180, ellipsis: true, render: (_, item) => item.effectiveModelKey ?? item.modelKey ?? "Unattended default" },
            { title: "Origin", key: "origin", width: 180,
              filters: [{ text: "Chat user request", value: "CurrentUserTurn" }, { text: "Admin owner", value: "AdminOwner" }],
              onFilter: (value, item) => item.authorizationOrigin === value,
              render: (_, item) => item.authorizationOrigin === "CurrentUserTurn" ? "Chat user request" : "Admin owner" }
          ]}
          expandable={{ fixed: "left", expandedRowKeys: expanded, onExpand: (open, item) => setExpanded(open ? [item.automationId] : []),
            expandedRowRender: item => <Flex vertical gap={token.padding} style={{ whiteSpace: "normal" }} role="region" aria-label="Automation details">
            <Descriptions bordered column={1} size="small" {...detailLayout}>
            <Descriptions.Item label="Instructions"><span style={{ whiteSpace: "pre-wrap" }}>{item.instructions}</span></Descriptions.Item>
            <Descriptions.Item label="Originally created from">{item.authorizationOrigin === "CurrentUserTurn" ? "Chat user request" : "Admin owner"}{item.sourceSessionId ? <> · <ConversationDestination sessionId={item.sourceSessionId} /></> : null}</Descriptions.Item>
            <Descriptions.Item label="Destination"><Flex vertical gap={token.paddingXS}><AutomationDestination target={item.executionTarget} delivery={item.completionDelivery} /></Flex></Descriptions.Item>
            {item.suspensionReason ? <Descriptions.Item label="Unavailable">{item.suspensionReason}</Descriptions.Item> : null}
            <Descriptions.Item label="When"><Flex vertical gap={token.paddingXS}>{item.triggers.map(t => t.kind === "schedule" ? <span key={t.triggerId}>{scheduleTimingLabel(t.schedule)}</span> : <Button key={t.triggerId} type="link" size="small" onClick={() => navigateToAppPath(`${adminHomePath("events")}?${t.source?.kind === "builtin" ? `builtin=${encodeURIComponent(t.source.key)}` : `event=${t.source?.eventId ?? ""}`}`)}>{childName(t)}{t.source.kind === "webhook" ? ` · ${catalog.find(e => eventSourceKey(e.source) === eventSourceKey(t.source))?.key ?? t.source.eventId}` : ""} · {t.enabled ? "Enabled" : "Disabled"}</Button>)}</Flex></Descriptions.Item>
            <Descriptions.Item label="Model">{item.effectiveModelKey ?? item.modelKey ?? "Unattended default"}</Descriptions.Item>
            <Descriptions.Item label="Last run">{runStatusLabel(item.executionStatus)}{item.outcome ? ` · ${runOutcomeLabel(item.outcome)}` : ""}</Descriptions.Item></Descriptions>
            <Flex wrap gap={token.paddingXS}><Button aria-label="Run automation now" aria-busy={item.automationId in pending} loading={item.automationId in pending}
              disabled={busy || !item.enabled || item.automationId in pending || activeWork.includes(item.executionStatus ?? "")}
              onClick={() => void mutate(`automations/${item.automationId}/run`, { expectedRevision: item.revision })}>{item.automationId in pending ? "Starting…" : "Run now"}</Button>
              <Button disabled={busy || terminal.includes(item.status)} onClick={() => edit(item)}>Edit automation</Button>
              <Button disabled={busy || terminal.includes(item.status)} onClick={() => void mutate(`automations/${item.automationId}`, {
                expectedRevision: item.revision, enabled: !item.enabled, name: item.name, instructions: item.instructions, triggers: item.triggers.map(t => t.kind === "schedule" ? t : { triggerId: t.triggerId, revision: t.revision, enabled: t.enabled, kind: "event", source: t.source, filterExpression: t.filterExpression, dispatch: t.dispatch }), modelKey: item.modelKey, reasoningEffort: item.reasoningEffort, executionTarget: item.executionTarget, completionDelivery: item.completionDelivery, requiresTools: item.requiresTools, requiresVision: item.requiresVision
              }, "PUT")}>{item.enabled ? "Disable automation" : "Enable automation"}</Button>
              <Button danger disabled={busy || item.status === "Cancelled"} onClick={() => {
                let confirmed = false;
                confirmAction(modal, { title: "Delete this automation?", content: "Stops future runs. Existing runs remain available in Runs.", okText: "Delete automation", danger: true,
                  onOk: async () => { confirmed = true; await mutate(`automations/${item.automationId}`, { expectedRevision: item.revision }, "DELETE"); },
                  afterClose: () => { if (confirmed) requestAnimationFrame(() => newButton.current?.focus()); }
                });
              }}>Delete automation</Button>
              {item.lastAgentRunId ? <Button onClick={() => onWork(item.lastAgentRunId!)}>View last run</Button> : null}</Flex>
            </Flex>
          }} />
        </> : null}
    </Flex></div>
  </section>;
}

export function scheduleTimingLabel(timing: ScheduleTiming) {
  if (timing.kind === "oneShot") return `Once · ${date(timing.atUtc ?? null)}`;
  if (timing.kind === "fixedInterval") return `Every ${timing.interval} seconds`;
  const days = timing.weekdays?.map(day => ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"][day]).join(", ");
  return `Every ${timing.interval} ${timing.kind === "daily" ? "day" : "week"}${timing.interval === 1 ? "" : "s"}${days ? ` · ${days}` : ""} · ${timing.localTime} · ${timing.timeZone}`;
}

function automationWhen(item: Automation, sources: AdminWebhookEvent[]) {
  return item.triggers.map(t => t.kind === "schedule" ? scheduleTimingLabel(t.schedule) : t.source?.kind === "builtin" ? `Built-in · ${t.source.key}`
    : sources.find(e => e.eventId === (t.source?.kind === "webhook" ? t.source.eventId : ""))?.displayName ?? "Webhook unavailable").join(" OR ");
}

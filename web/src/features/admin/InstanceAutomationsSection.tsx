import { AdminErrorNotice } from "./adminFailure";
import { useCallback, useEffect, useRef, useState, type Key } from "react";
import { Alert, App, Button, DatePicker, Descriptions, Empty, Flex, Form, Input, InputNumber, Select, Spin, Switch, Table, Tag, Typography, theme } from "antd";
import dayjs from "dayjs";
import { confirmAction } from "../../app/confirmAction";
import { listModels, type ModelDescriptor } from "../../services/api";
import { instanceContinuityRequest as request, type Automation, type AutomationDraft, type AutomationReview, type ScheduleTiming, listEventSources, type AdminEventSource } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { ExecutionModelFields } from "./ExecutionModelFields";
import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";
import { useAdminDetailLayout } from "./useAdminDetailLayout";

import { runStatusLabel, runOutcomeLabel, type AutomationSelection } from "../chat/runPresentation";
import { useAutomationSelection } from "./useAutomationSelection";

const activeWork = ["Queued", "Running", "WaitingForApproval", "WaitingToRetry"];
const terminal = ["Completed", "Cancelled", "Expired"];
const date = (value: string | null) => value ? new Date(value).toLocaleString() : "Not scheduled";
const defaultTiming = (): ScheduleTiming => ({ kind: "daily", timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC", interval: 1, localTime: "09:00" });
const blank = (): AutomationDraft => ({ expectedRevision: 0, enabled: true, name: "", instructions: "", modelKey: null, reasoningEffort: null,
  trigger: { kind: "schedule", schedule: defaultTiming() } });

export function InstanceAutomationsSection({ instanceId, onWork, selection, active = true }: { instanceId: string; active?: boolean; onWork: (workId?: string) => void; selection?: AutomationSelection }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const newButton = useRef<HTMLButtonElement>(null);
  const [savedSelection, setSavedSelection] = useState<AutomationSelection>();
  useEffect(() => setSavedSelection(undefined), [selection]);
  const [expanded, setExpanded] = useState<Key[]>([]);
  const detailLayout = useAdminDetailLayout();
  const [review, setReview] = useState<AutomationReview | null>(null);
  const [sources, setSources] = useState<AdminEventSource[]>([]);
  const [models, setModels] = useState<ModelDescriptor[]>([]);
  const [draft, setDraft] = useState<AutomationDraft>(blank);
  const [editor, setEditor] = useState<string | null>(null);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false);
  const [pending, setPending] = useState<Record<string, string | null>>({});
  const order = useRef({ generation: 0, mutating: false, pending: {} as Record<string, string | null> });
  const apply = useCallback((next: AutomationReview) => {
    order.current.pending = Object.fromEntries(Object.entries(order.current.pending).filter(([id, prior]) => {
      const item = next.items.find(i => i.automationId === id);
      return item && (!item.lastWorkItemId || item.lastWorkItemId === prior);
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
    void listEventSources().then(result => setSources(result)).catch(reason => setError(describeAdminError(reason, "Event Sources could not be loaded.")));
    void listModels().then(result => setModels(result.models.filter(m => m.tools))).catch(reason => setError(describeAdminError(reason, "Execution models could not be loaded.")));
    return () => { order.current.generation++; };
  }, [reload]);
  useEffect(() => {
    if (loading || !active) return;
    const timer = window.setInterval(() => { void reload(); }, 5000); return () => window.clearInterval(timer);
  }, [loading, reload, active]);
  const previousActive = useRef(active);
  useEffect(() => {
    if (active && !previousActive.current) void reload();
    previousActive.current = active;
  }, [active, reload]);
  useEffect(() => {
    if (selection?.kind === "automation") void reload(true);
  }, [selection, reload]);
  async function mutate(path: string, body: unknown, method = "POST") {
    const runId = /^automations\/([^/]+)\/run$/.exec(path)?.[1];
    if (order.current.mutating || runId && runId in order.current.pending) return;
    order.current.mutating = true; ++order.current.generation; setBusy(true); setError(null);
    if (runId) { order.current.pending = { ...order.current.pending, [runId]: review?.items.find(i => i.automationId === runId)?.lastWorkItemId ?? null }; setPending(order.current.pending); }
    let accepted = false;
    try {
      const saved = await request<Automation>(instanceId, path, method, body); accepted = true;
      if (saved?.automationId && method === "PUT") setSavedSelection({ kind: "automation", automationId: saved.automationId, request: Date.now() });
      if (method === "DELETE") { setExpanded([]); setSavedSelection(undefined); }
      if (path === "automations" || method === "PUT") { setEditor(null); setDraft(blank()); }
      apply(await request<AutomationReview>(instanceId, "automations"));
      if (path === "automations" && saved?.automationId) requestAnimationFrame(() =>
        document.querySelector<HTMLButtonElement>(`[data-automation-id="${saved.automationId}"]`)?.focus());
    } catch (reason) {
      if (runId && !accepted) { const next = { ...order.current.pending }; delete next[runId]; order.current.pending = next; setPending(next); }
      setError(describeAdminError(reason, "Automation update failed. Reload for the current revision."));
    } finally { order.current.mutating = false; setBusy(false); }
  }
  const tableVersion = useAutomationSelection(savedSelection ?? selection, "automation", review?.items.map(item => item.automationId) ?? [], setSearch, setExpanded);
  const timing = draft.trigger.kind === "schedule" ? draft.trigger.schedule : defaultTiming();
  const isSchedule = draft.trigger.kind === "schedule";
  const viewerZone = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  const previewTime = (value?: string | null) => value && Number.isFinite(Date.parse(value))
    ? `${new Date(value).toLocaleString()} (${viewerZone})` : null;
  const policy = review?.policy;
  const minimum = timing.kind === "fixedInterval" ? policy?.minFixedIntervalSeconds ?? 60 : timing.kind === "weekly" ? Math.ceil((policy?.minRecurrenceDays ?? 1) / 7) : policy?.minRecurrenceDays ?? 1;
  const kindAllowed = !policy || ({ oneShot: policy.allowOneShot, daily: policy.allowDaily, weekly: policy.allowWeekly, fixedInterval: policy.allowFixedInterval })[timing.kind];
  const hasEnd = !!timing.maxOccurrences || (timing.kind === "fixedInterval" ? !!timing.endAtUtc : !!timing.endDate);
  const maximum = timing.kind === "fixedInterval" ? 604800 : timing.kind === "daily" ? 365 : 52;
  const scheduleValid = kindAllowed && (timing.kind === "oneShot" || policy?.allowIndefiniteRecurrence !== false || hasEnd) &&
    (timing.maxOccurrences == null || Number.isInteger(timing.maxOccurrences) && timing.maxOccurrences >= 1) && draft.instructions.trim().length > 0 && draft.instructions.trim().length <= 2000 &&
    (timing.kind === "oneShot" ? !!timing.atUtc && Number.isFinite(Date.parse(timing.atUtc)) :
      timing.interval >= minimum && timing.interval <= maximum && (timing.kind === "fixedInterval" ||
        /^\d{2}:\d{2}$/.test(timing.localTime ?? "") && !!timing.timeZone.trim() && (timing.kind !== "weekly" || !!timing.weekdays?.length)));
  const valid = draft.name.trim().length > 0 && draft.name.trim().length <= 120 && draft.instructions.trim().length > 0 && draft.instructions.trim().length <= 2000
    && (isSchedule ? scheduleValid : draft.trigger.kind === "event" && sources.some(source => source.sourceId === (draft.trigger.kind === "event" ? draft.trigger.eventSourceId : "") && source.status === "Active"));
  function setTiming(change: Partial<ScheduleTiming>) { setDraft({ ...draft, trigger: { kind: "schedule", schedule: { ...timing, ...change } } }); }
  function edit(item: Automation) { setEditor(item.automationId); setDraft({ expectedRevision: item.revision, enabled: item.enabled, name: item.name, instructions: item.instructions,
    trigger: item.trigger, modelKey: item.modelKey, reasoningEffort: item.reasoningEffort }); }
  return <section className="admin-definition-panel" aria-label="Automations">
    <div className="admin-definition-panel-heading"><Typography.Title level={4}>Automations</Typography.Title>
      <Typography.Text type="secondary">Choose when the agent follows your instructions. Each Run uses its authorized capabilities and normal approvals.</Typography.Text></div>
    <div className="admin-definition-panel-body"><Flex vertical gap={token.padding}>
      {loading ? <Spin aria-label="Loading automations" /> : null}
      {selection?.kind === "automation" && !loading && !error && review && !review.items.some(item => item.automationId === selection.automationId) ? <Alert type="info" showIcon title="This source configuration is no longer available" description="It may have been deleted or retired. Its run remains available in Runs." /> : null}
      {error ? <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button disabled={busy} onClick={() => void reload()}>Reload automations</Button>} /> : null}
      <Flex wrap gap={token.paddingXS}><Button ref={newButton} disabled={busy} onClick={() => { setDraft(blank()); setEditor("new"); }}>New automation</Button>
        <Button disabled={busy} onClick={() => void reload()}>Refresh automations</Button><Button onClick={() => onWork()}>View runs</Button></Flex>
      {editor ? <Form layout="vertical" className="admin-config-form" onKeyDown={event => {
        if (event.key === "Enter" && (event.target as HTMLElement).closest(".ant-picker")) event.preventDefault();
      }} onFinish={() => {
        const body = { ...draft, trigger: isSchedule ? { kind: "schedule", schedule: timing.kind === "fixedInterval" && !timing.anchorAtUtc ? { ...timing, anchorAtUtc: new Date(Date.now() + timing.interval * 1000).toISOString() } : timing } : draft.trigger };
        void mutate(editor === "new" ? "automations" : `automations/${editor}`, body, editor === "new" ? "POST" : "PUT");
      }}>
        <Form.Item label="Name"><Input aria-label="Automation name" maxLength={120} value={draft.name} disabled={busy} onChange={e => setDraft({ ...draft, name: e.target.value })} /></Form.Item>
        <Form.Item label="Instructions" extra={`${draft.instructions.length} / 2000 characters`}><Input.TextArea aria-label="Automation instructions" rows={4} maxLength={2000} value={draft.instructions} disabled={busy} onChange={e => setDraft({ ...draft, instructions: e.target.value })} /></Form.Item>
        <Form.Item label="When"><Select aria-label="Automation trigger" value={draft.trigger.kind} disabled={busy} options={[{ value: "schedule", label: "Schedule" }, { value: "event", label: "Event" }]}
          onChange={kind => setDraft({ ...draft, trigger: kind === "schedule" ? { kind, schedule: defaultTiming() } : { kind: "event", eventSourceId: "", eventType: "order.placed" } })} /></Form.Item>
        {draft.trigger.kind === "event" ? <>
          <Form.Item label="Event Source"><Select aria-label="Automation event source" value={draft.trigger.eventSourceId || undefined} disabled={busy} placeholder="Select an active Event Source"
            options={sources.map(source => ({ value: source.sourceId, label: source.displayName, disabled: source.status !== "Active" }))}
            onChange={eventSourceId => setDraft({ ...draft, trigger: { kind: "event", eventSourceId, eventType: "order.placed" } })} /></Form.Item>
          <Form.Item label="Event"><Select aria-label="Automation event type" value="order.placed" disabled={busy} options={[{ value: "order.placed", label: "Order placed" }]} /></Form.Item>
          {!sources.some(source => source.status === "Active") ? <Alert showIcon type="info" title="Create an Event Source in Connections before enabling an Event Automation." /> : null}
        </> : <>
        <Flex wrap gap={token.padding} className="admin-form-row">
          <Form.Item label="Timing"><Select aria-label="Schedule timing" style={{ minWidth: "10rem" }} value={timing.kind} disabled={busy}
            options={[{ value: "oneShot", label: "Once", disabled: policy?.allowOneShot === false }, { value: "daily", label: "Daily", disabled: policy?.allowDaily === false }, { value: "weekly", label: "Weekly", disabled: policy?.allowWeekly === false }, { value: "fixedInterval", label: "Fixed interval", disabled: policy?.allowFixedInterval === false }]}
            onChange={kind => setTiming({ kind, timeZone: timing.timeZone, interval: kind === "fixedInterval" ? 3600 : 1, localTime: "09:00", weekdays: kind === "weekly" ? [1] : null })} /></Form.Item>
          {timing.kind === "oneShot" ? <Form.Item label="Run at" extra={`Times shown in ${viewerZone}. Saved in UTC.`}>
            <DatePicker aria-label="Schedule run at" showTime={{ format: "HH:mm" }} format="YYYY-MM-DD HH:mm"
              value={timing.atUtc ? dayjs(timing.atUtc) : null} disabled={busy}
              onChange={value => setTiming({ atUtc: value?.toISOString() ?? null })} />
          </Form.Item> :
            <Form.Item style={{ minWidth: "9rem" }} label={timing.kind === "fixedInterval" ? "Interval (seconds)" : timing.kind === "daily" ? "Every (days)" : "Every (weeks)"}>
              <InputNumber aria-label={timing.kind === "fixedInterval" ? "Schedule interval (seconds)" : timing.kind === "daily" ? "Schedule every (days)" : "Schedule every (weeks)"} min={minimum} max={maximum} precision={0} value={timing.interval} disabled={busy} onChange={value => setTiming({ interval: value ?? 0 })} /></Form.Item>}
          {timing.kind === "daily" || timing.kind === "weekly" ? <><Form.Item label="Local time"><Input aria-label="Schedule local time" type="time" value={timing.localTime ?? "09:00"} disabled={busy} onChange={e => setTiming({ localTime: e.target.value })} /></Form.Item>
            <Form.Item label="Time zone"><Input aria-label="Schedule time zone" value={timing.timeZone} disabled={busy} onChange={e => setTiming({ timeZone: e.target.value })} /></Form.Item></> : null}
        </Flex>
        {timing.kind === "weekly" ? <Form.Item label="Weekdays"><Select mode="multiple" aria-label="Schedule weekdays" value={timing.weekdays ?? []} disabled={busy}
          options={["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"].map((label, value) => ({ label, value }))} onChange={weekdays => setTiming({ weekdays })} /></Form.Item> : null}
        {timing.kind !== "oneShot" ? <>
          {policy?.allowIndefiniteRecurrence === false ? <Alert type="info" showIcon title="This Definition requires an end date or occurrence limit." /> : null}
          <Flex wrap gap={token.padding} className="admin-form-row">
            <Form.Item label={timing.kind === "fixedInterval" ? "End at" : "End date"}
              extra={timing.kind === "fixedInterval" ? `Times shown in ${viewerZone}. Saved in UTC.` : undefined}>
              <DatePicker aria-label={timing.kind === "fixedInterval" ? "Schedule end at" : "Schedule end date"}
                showTime={timing.kind === "fixedInterval" ? { format: "HH:mm" } : false}
                format={timing.kind === "fixedInterval" ? "YYYY-MM-DD HH:mm" : "YYYY-MM-DD"}
                value={timing.kind === "fixedInterval" ? timing.endAtUtc ? dayjs(timing.endAtUtc) : null : timing.endDate ? dayjs(timing.endDate) : null}
                disabled={busy} onChange={value => setTiming(timing.kind === "fixedInterval"
                  ? { endAtUtc: value?.toISOString() ?? null } : { endDate: value?.format("YYYY-MM-DD") ?? null })} />
            </Form.Item>
            <Form.Item label="Maximum occurrences" extra="Leave both bounds empty only if the Definition permits ongoing recurrence.">
              <InputNumber aria-label="Schedule maximum occurrences" min={1} precision={0} value={timing.maxOccurrences} disabled={busy} onInput={text => setTiming({ maxOccurrences: text.trim() ? Number(text) : null })} onChange={value => setTiming({ maxOccurrences: value })} />
            </Form.Item>
          </Flex>
        </> : null}
        {timing.kind === "oneShot" && previewTime(timing.atUtc) ? <Typography.Paragraph type="secondary" role="status">
          Runs on {previewTime(timing.atUtc)}.
        </Typography.Paragraph> : null}
        {timing.kind === "fixedInterval" && previewTime(timing.endAtUtc) ? <Typography.Paragraph type="secondary" role="status">
          Stops on {previewTime(timing.endAtUtc)}.
        </Typography.Paragraph> : null}
        </>}
        <Form.Item label="Enabled"><Switch aria-label="Enable automation" checked={draft.enabled} disabled={busy} onChange={enabled => setDraft({ ...draft, enabled })} /></Form.Item>
        <Form.Item label="Execution model" extra="Uses the instance unattended default unless you select a model. Each admitted run keeps its model."><ExecutionModelFields models={models}
          modelKey={draft.modelKey ?? ""} reasoningEffort={draft.reasoningEffort ?? ""} disabled={busy} modelLabel="Automation execution model" effortLabel="Automation reasoning effort" defaultLabel="Unattended default"
          onChange={(modelKey, reasoningEffort) => setDraft({ ...draft, modelKey: modelKey || null, reasoningEffort: reasoningEffort || null })} /></Form.Item>
        <Flex wrap gap={token.paddingXS}><Button type="primary" htmlType="submit" disabled={!valid || busy} loading={busy}>{editor === "new" ? "Create automation" : "Save automation"}</Button>
          <Button disabled={busy} onClick={() => setEditor(null)}>Cancel automation edit</Button></Flex>
      </Form> : null}
      {review ? review.items.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No automations yet. Create one here or ask the agent in Chat to do something later." /> :
        <>
        <AdminCollectionToolbar label="automations" value={search} onChange={setSearch} />
        <Table<Automation> key={tableVersion} aria-label="Automations table" className="admin-collection-table" size="small" rowKey="automationId"
          dataSource={review.items.filter(item => [item.name, item.instructions, item.status, automationWhen(item, sources),
            item.authorizationOrigin === "CurrentUserTurn" ? "Chat user request" : "Admin owner", item.sourceSessionId ?? "",
            item.effectiveModelKey ?? item.modelKey ?? "Unattended default", item.executionStatus ?? ""]
            .some(value => value.toLowerCase().includes(search.trim().toLowerCase()))).sort((a, b) => Number(b.automationId === selection?.automationId) - Number(a.automationId === selection?.automationId))}
          scroll={{ x: 1470 }} pagination={pagination}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No matches. Clear search or filters to see all results." /> }}
          columns={[
            { title: "Name", key: "task", width: 350, ellipsis: true,
              render: (_, item) => <Button type="link" size="small" className="admin-collection-name" title={item.name}
                data-automation-id={item.automationId} aria-label={`View automation: ${item.name}`} aria-expanded={expanded.includes(item.automationId)}
                onClick={() => setExpanded(expanded.includes(item.automationId) ? [] : [item.automationId])}>{item.name}</Button> },
            { title: "When", key: "timing", width: 240, ellipsis: true, render: (_, item) => <span title={automationWhen(item, sources)}>{automationWhen(item, sources)}</span> },
            { title: "Status", dataIndex: "status", width: 120,
              filters: [...new Set(review.items.map(item => item.status))].map(value => ({ text: value, value })),
              onFilter: (value, item) => item.status === value, render: (status: string) => <Tag>{status}</Tag> },
            { title: "Next run", key: "next", width: 220,
              sorter: (a, b) => (a.enabled && !terminal.includes(a.status) ? a.nextRunAt ?? "" : "").localeCompare(b.enabled && !terminal.includes(b.status) ? b.nextRunAt ?? "" : ""),
              render: (_, item) => item.enabled && !terminal.includes(item.status) && item.trigger.kind === "event" ? "On event" : date(item.enabled && !terminal.includes(item.status) ? item.nextRunAt : null) },
            { title: "Last run", key: "execution", width: 180,
              filters: [...new Set(review.items.map(item => item.executionStatus ?? "Not yet"))].map(value => ({ text: runStatusLabel(value), value })),
              onFilter: (value, item) => (item.executionStatus ?? "Not yet") === value,
              render: (_, item) => item.lastWorkItemId ? <Button type="link" size="small" aria-label={`View last run: ${item.name}`} onClick={() => onWork(item.lastWorkItemId!)}>{runStatusLabel(item.executionStatus ?? "View run")}{item.outcome ? ` · ${runOutcomeLabel(item.outcome)}` : ""}</Button> : "Not yet" },
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
            <Descriptions.Item label="Originally created from">{item.authorizationOrigin === "CurrentUserTurn" ? "Chat user request" : "Admin owner"}{item.sourceSessionId ? ` · Session ${item.sourceSessionId}` : ""}</Descriptions.Item>
            <Descriptions.Item label="When">{automationWhen(item, sources)}</Descriptions.Item>
            <Descriptions.Item label="Model">{item.effectiveModelKey ?? item.modelKey ?? "Unattended default"}</Descriptions.Item>
            <Descriptions.Item label="Last run">{runStatusLabel(item.executionStatus)}{item.outcome ? ` · ${runOutcomeLabel(item.outcome)}` : ""}</Descriptions.Item></Descriptions>
            <Flex wrap gap={token.paddingXS}><Button aria-label="Run automation now" aria-busy={item.automationId in pending} loading={item.automationId in pending}
              disabled={busy || !item.enabled || item.automationId in pending || activeWork.includes(item.executionStatus ?? "")}
              onClick={() => void mutate(`automations/${item.automationId}/run`, { expectedRevision: item.revision })}>{item.automationId in pending ? "Starting…" : "Run now"}</Button>
              <Button disabled={busy || terminal.includes(item.status)} onClick={() => edit(item)}>Edit automation</Button>
              <Button disabled={busy || terminal.includes(item.status)} onClick={() => void mutate(`automations/${item.automationId}`, {
                expectedRevision: item.revision, enabled: !item.enabled, name: item.name, instructions: item.instructions, trigger: item.trigger, modelKey: item.modelKey, reasoningEffort: item.reasoningEffort
              }, "PUT")}>{item.enabled ? "Disable automation" : "Enable automation"}</Button>
              <Button danger disabled={busy || item.status === "Cancelled"} onClick={() => {
                let confirmed = false;
                confirmAction(modal, { title: "Delete this automation?", content: "Stops future runs. Existing runs remain available in Runs.", okText: "Delete automation", danger: true,
                  onOk: async () => { confirmed = true; await mutate(`automations/${item.automationId}`, { expectedRevision: item.revision }, "DELETE"); },
                  afterClose: () => { if (confirmed) requestAnimationFrame(() => newButton.current?.focus()); }
                });
              }}>Delete automation</Button>
              {item.lastWorkItemId ? <Button onClick={() => onWork(item.lastWorkItemId!)}>View last run</Button> : null}</Flex>
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

function automationWhen(item: Automation, sources: AdminEventSource[]) {
  return item.trigger.kind === "schedule" ? scheduleTimingLabel(item.trigger.schedule)
    : `Order placed · ${sources.find(source => source.sourceId === (item.trigger.kind === "event" ? item.trigger.eventSourceId : ""))?.displayName ?? "Event Source unavailable"}`;
}

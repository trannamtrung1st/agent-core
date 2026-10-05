import { useCallback, useEffect, useRef, useState, type Key } from "react";
import { Alert, App, Button, Descriptions, Empty, Flex, Form, Input, InputNumber, Select, Spin, Switch, Table, Tag, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import { listModels, type ModelDescriptor } from "../../services/api";
import { instanceContinuityRequest as request, type OwnerSchedule, type OwnerScheduleDraft, type OwnerScheduleReview, type ScheduleTiming } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";
import { ExecutionModelFields } from "./ExecutionModelFields";
import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";
import { useAdminDetailLayout } from "./useAdminDetailLayout";

const activeWork = ["Queued", "Running", "WaitingForApproval", "WaitingToRetry"];
const terminal = ["Completed", "Cancelled", "Expired"];
const date = (value: string | null) => value ? new Date(value).toLocaleString() : "Not scheduled";
const blank = (): OwnerScheduleDraft => ({ expectedRevision: 0, enabled: true, intent: "", modelKey: null, reasoningEffort: null,
  schedule: { kind: "daily", timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC", interval: 1, localTime: "09:00" } });

export function InstanceSchedulesSection({ instanceId, onWork }: { instanceId: string; onWork: () => void }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const [expanded, setExpanded] = useState<Key[]>([]);
  const detailLayout = useAdminDetailLayout();
  const [review, setReview] = useState<OwnerScheduleReview | null>(null);
  const [models, setModels] = useState<ModelDescriptor[]>([]);
  const [draft, setDraft] = useState<OwnerScheduleDraft>(blank);
  const [editor, setEditor] = useState<string | null>(null);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false);
  const [pending, setPending] = useState<Record<string, string | null>>({});
  const order = useRef({ generation: 0, mutating: false, pending: {} as Record<string, string | null> });
  const apply = useCallback((next: OwnerScheduleReview) => {
    order.current.pending = Object.fromEntries(Object.entries(order.current.pending).filter(([id, prior]) => {
      const item = next.items.find(i => i.registrationId === id);
      return item && (!item.lastWorkItemId || item.lastWorkItemId === prior);
    }));
    setPending(order.current.pending); setReview(next);
  }, []);
  const reload = useCallback(async (initial = false) => {
    if (order.current.mutating) return;
    const generation = ++order.current.generation;
    if (initial) setLoading(true);
    try {
      const next = await request<OwnerScheduleReview>(instanceId, "schedules");
      if (generation === order.current.generation) { apply(next); setError(null); }
    } catch (reason) { if (generation === order.current.generation) setError(describeAdminError(reason, "Schedules could not be loaded. Reload to retry.")); }
    finally { if (generation === order.current.generation) setLoading(false); }
  }, [instanceId, apply]);
  useEffect(() => {
    void reload(true);
    void listModels().then(result => setModels(result.models.filter(m => m.tools))).catch(reason => setError(describeAdminError(reason, "Execution models could not be loaded.")));
    return () => { order.current.generation++; };
  }, [reload]);
  useEffect(() => {
    if (loading) return;
    const timer = window.setInterval(() => { void reload(); }, 5000); return () => window.clearInterval(timer);
  }, [loading, reload]);
  async function mutate(path: string, body: unknown, method = "POST") {
    const runId = /^schedules\/([^/]+)\/run$/.exec(path)?.[1];
    if (order.current.mutating || runId && runId in order.current.pending) return;
    order.current.mutating = true; ++order.current.generation; setBusy(true); setError(null);
    if (runId) { order.current.pending = { ...order.current.pending, [runId]: review?.items.find(i => i.registrationId === runId)?.lastWorkItemId ?? null }; setPending(order.current.pending); }
    let accepted = false;
    try {
      await request(instanceId, path, method, body); accepted = true;
      if (path === "schedules" || method === "PUT") { setEditor(null); setDraft(blank()); }
      apply(await request<OwnerScheduleReview>(instanceId, "schedules"));
    } catch (reason) {
      if (runId && !accepted) { const next = { ...order.current.pending }; delete next[runId]; order.current.pending = next; setPending(next); }
      setError(describeAdminError(reason, "Schedule update failed. Reload for the current revision."));
    } finally { order.current.mutating = false; setBusy(false); }
  }
  const timing = draft.schedule;
  const policy = review?.policy;
  const minimum = timing.kind === "fixedInterval" ? policy?.minFixedIntervalSeconds ?? 60 : timing.kind === "weekly" ? Math.ceil((policy?.minRecurrenceDays ?? 1) / 7) : policy?.minRecurrenceDays ?? 1;
  const kindAllowed = !policy || ({ oneShot: policy.allowOneShot, daily: policy.allowDaily, weekly: policy.allowWeekly, fixedInterval: policy.allowFixedInterval })[timing.kind];
  const hasEnd = !!timing.maxOccurrences || (timing.kind === "fixedInterval" ? !!timing.endAtUtc : !!timing.endDate);
  const maximum = timing.kind === "fixedInterval" ? 604800 : timing.kind === "daily" ? 365 : 52;
  const valid = kindAllowed && (timing.kind === "oneShot" || policy?.allowIndefiniteRecurrence !== false || hasEnd) &&
    (timing.maxOccurrences == null || Number.isInteger(timing.maxOccurrences) && timing.maxOccurrences >= 1) && draft.intent.trim().length > 0 && draft.intent.trim().length <= 500 &&
    (timing.kind === "oneShot" ? !!timing.atUtc && Number.isFinite(Date.parse(timing.atUtc)) :
      timing.interval >= minimum && timing.interval <= maximum && (timing.kind === "fixedInterval" ||
        /^\d{2}:\d{2}$/.test(timing.localTime ?? "") && !!timing.timeZone.trim() && (timing.kind !== "weekly" || !!timing.weekdays?.length)));
  function setTiming(change: Partial<ScheduleTiming>) { setDraft({ ...draft, schedule: { ...timing, ...change } }); }
  function edit(item: OwnerSchedule) { setEditor(item.registrationId); setDraft({ expectedRevision: item.revision, enabled: item.enabled, intent: item.intent,
    schedule: item.schedule, modelKey: item.modelKey, reasoningEffort: item.reasoningEffort }); }
  return <section className="admin-definition-panel" aria-label="Scheduled work">
    <div className="admin-definition-panel-heading"><Typography.Title level={4}>Scheduled work</Typography.Title>
      <Typography.Text type="secondary">Configure a known task for later. Chat and Admin use the same schedules; each run follows normal tools and approvals.</Typography.Text></div>
    <div className="admin-definition-panel-body"><Flex vertical gap={token.padding}>
      {loading ? <Spin aria-label="Loading schedules" /> : null}
      {error ? <Alert type="error" showIcon title={error.message} action={<Button disabled={busy} onClick={() => void reload()}>Reload schedules</Button>}
        description={error.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: error.diagnosticId }} /> : undefined} /> : null}
      <Flex wrap gap={token.paddingXS}><Button disabled={busy} onClick={() => { setDraft(blank()); setEditor("new"); }}>New schedule</Button>
        <Button disabled={busy} onClick={() => void reload()}>Refresh schedules</Button><Button onClick={onWork}>View scheduled executions</Button></Flex>
      {editor ? <Form layout="vertical" onFinish={() => {
        const body = { ...draft, schedule: timing.kind === "fixedInterval" && !timing.anchorAtUtc ? { ...timing, anchorAtUtc: new Date(Date.now() + timing.interval * 1000).toISOString() } : timing };
        void mutate(editor === "new" ? "schedules" : `schedules/${editor}`, body, editor === "new" ? "POST" : "PUT");
      }}>
        <Form.Item label="Task" extra={`${draft.intent.length} / 500 characters`}><Input.TextArea aria-label="Schedule task" rows={3} maxLength={500} value={draft.intent} disabled={busy} onChange={e => setDraft({ ...draft, intent: e.target.value })} /></Form.Item>
        <Flex wrap gap={token.padding}>
          <Form.Item label="Timing"><Select aria-label="Schedule timing" style={{ minWidth: "10rem" }} value={timing.kind} disabled={busy}
            options={[{ value: "oneShot", label: "Once", disabled: policy?.allowOneShot === false }, { value: "daily", label: "Daily", disabled: policy?.allowDaily === false }, { value: "weekly", label: "Weekly", disabled: policy?.allowWeekly === false }, { value: "fixedInterval", label: "Fixed interval", disabled: policy?.allowFixedInterval === false }]}
            onChange={kind => setDraft({ ...draft, schedule: { kind, timeZone: timing.timeZone, interval: kind === "fixedInterval" ? 3600 : 1, localTime: "09:00", weekdays: kind === "weekly" ? [1] : null } })} /></Form.Item>
          {timing.kind === "oneShot" ? <Form.Item label="Run at (UTC)" extra="Enter an ISO timestamp, for example 2026-10-06T02:00:00Z."><Input aria-label="Schedule run at" value={timing.atUtc ?? ""} disabled={busy} onChange={e => setTiming({ atUtc: e.target.value })} /></Form.Item> :
            <Form.Item label={timing.kind === "fixedInterval" ? "Interval (seconds)" : timing.kind === "daily" ? "Every (days)" : "Every (weeks)"}>
              <InputNumber aria-label="Schedule interval" min={minimum} max={maximum} precision={0} value={timing.interval} disabled={busy} onChange={value => setTiming({ interval: value ?? 0 })} /></Form.Item>}
          {timing.kind === "daily" || timing.kind === "weekly" ? <><Form.Item label="Local time"><Input aria-label="Schedule local time" type="time" value={timing.localTime ?? "09:00"} disabled={busy} onChange={e => setTiming({ localTime: e.target.value })} /></Form.Item>
            <Form.Item label="Time zone"><Input aria-label="Schedule time zone" value={timing.timeZone} disabled={busy} onChange={e => setTiming({ timeZone: e.target.value })} /></Form.Item></> : null}
          <Form.Item label="Enabled"><Switch aria-label="Enable schedule" checked={draft.enabled} disabled={busy} onChange={enabled => setDraft({ ...draft, enabled })} /></Form.Item>
        </Flex>
        {timing.kind === "weekly" ? <Form.Item label="Weekdays"><Select mode="multiple" aria-label="Schedule weekdays" value={timing.weekdays ?? []} disabled={busy}
          options={["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"].map((label, value) => ({ label, value }))} onChange={weekdays => setTiming({ weekdays })} /></Form.Item> : null}
        {timing.kind !== "oneShot" ? <>
          {policy?.allowIndefiniteRecurrence === false ? <Alert type="info" showIcon title="This Definition requires an end date or occurrence limit." /> : null}
          <Flex wrap gap={token.padding}>
            <Form.Item label={timing.kind === "fixedInterval" ? "End at (UTC)" : "End date"}>
              <Input aria-label="Schedule end" type={timing.kind === "fixedInterval" ? "text" : "date"} value={(timing.kind === "fixedInterval" ? timing.endAtUtc : timing.endDate) ?? ""} disabled={busy}
                onChange={e => setTiming(timing.kind === "fixedInterval" ? { endAtUtc: e.target.value || null } : { endDate: e.target.value || null })} />
            </Form.Item>
            <Form.Item label="Maximum occurrences" extra="Leave both bounds empty only if the Definition permits ongoing recurrence.">
              <InputNumber aria-label="Schedule maximum occurrences" min={1} precision={0} value={timing.maxOccurrences} disabled={busy} onInput={text => setTiming({ maxOccurrences: text.trim() ? Number(text) : null })} onChange={value => setTiming({ maxOccurrences: value })} />
            </Form.Item>
          </Flex>
        </> : null}
        <Form.Item label="Execution model" extra="Uses the instance unattended default unless you select a model. Each admitted run keeps its model."><ExecutionModelFields models={models}
          modelKey={draft.modelKey ?? ""} reasoningEffort={draft.reasoningEffort ?? ""} disabled={busy} modelLabel="Schedule execution model" effortLabel="Schedule reasoning effort" defaultLabel="Unattended default"
          onChange={(modelKey, reasoningEffort) => setDraft({ ...draft, modelKey: modelKey || null, reasoningEffort: reasoningEffort || null })} /></Form.Item>
        <Flex wrap gap={token.paddingXS}><Button type="primary" htmlType="submit" disabled={!valid || busy} loading={busy}>{editor === "new" ? "Create schedule" : "Save schedule"}</Button>
          <Button disabled={busy} onClick={() => setEditor(null)}>Cancel schedule edit</Button></Flex>
      </Form> : null}
      {review ? review.items.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No scheduled work yet. Create a schedule here or ask the agent in Chat." /> :
        <>
        <AdminCollectionToolbar label="scheduled work" value={search} onChange={setSearch} />
        <Table<OwnerSchedule> aria-label="Scheduled work table" className="admin-collection-table" size="small" rowKey="registrationId"
          dataSource={review.items.filter(item => [item.intent, item.status, item.schedule.kind, item.schedule.timeZone,
            item.authorizationOrigin === "CurrentUserTurn" ? "Chat user request" : "Admin owner", item.sourceSessionId ?? "",
            item.effectiveModelKey ?? item.modelKey ?? "Unattended default", item.executionStatus ?? ""]
            .some(value => value.toLowerCase().includes(search.trim().toLowerCase())))}
          scroll={{ x: 1050 }} pagination={pagination}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No matches. Clear search or filters to see all results." /> }}
          columns={[
            { title: "Task", key: "task", width: 350, ellipsis: true,
              render: (_, item) => <Button type="link" size="small" className="admin-collection-name" title={item.intent}
                aria-label={`View schedule: ${item.intent}`} aria-expanded={expanded.includes(item.registrationId)}
                onClick={() => setExpanded(expanded.includes(item.registrationId) ? [] : [item.registrationId])}>{item.intent}</Button> },
            { title: "Status", dataIndex: "status", width: 120,
              filters: [...new Set(review.items.map(item => item.status))].map(value => ({ text: value, value })),
              onFilter: (value, item) => item.status === value, render: (status: string) => <Tag>{status}</Tag> },
            { title: "Next run", key: "next", width: 220,
              sorter: (a, b) => (a.enabled && !terminal.includes(a.status) ? a.nextRunAt ?? "" : "").localeCompare(b.enabled && !terminal.includes(b.status) ? b.nextRunAt ?? "" : ""),
              render: (_, item) => date(item.enabled && !terminal.includes(item.status) ? item.nextRunAt : null) },
            { title: "Latest execution", key: "execution", width: 180,
              filters: [...new Set(review.items.map(item => item.executionStatus ?? "Not yet"))].map(value => ({ text: value, value })),
              onFilter: (value, item) => (item.executionStatus ?? "Not yet") === value,
              render: (_, item) => item.executionStatus ?? "Not yet" },
            { title: "Origin", key: "origin", width: 180,
              filters: [{ text: "Chat user request", value: "CurrentUserTurn" }, { text: "Admin owner", value: "AdminOwner" }],
              onFilter: (value, item) => item.authorizationOrigin === value,
              render: (_, item) => item.authorizationOrigin === "CurrentUserTurn" ? "Chat user request" : "Admin owner" }
          ]}
          expandable={{ fixed: "left", expandedRowKeys: expanded, onExpand: (open, item) => setExpanded(open ? [item.registrationId] : []),
            expandedRowRender: item => <Flex vertical gap={token.padding} style={{ whiteSpace: "normal" }} role="region" aria-label="Schedule details">
            <Descriptions bordered column={1} size="small" {...detailLayout}>
            <Descriptions.Item label="Task"><span style={{ whiteSpace: "pre-wrap" }}>{item.intent}</span></Descriptions.Item>
            <Descriptions.Item label="Originally created from">{item.authorizationOrigin === "CurrentUserTurn" ? "Chat user request" : "Admin owner"}{item.sourceSessionId ? ` · Session ${item.sourceSessionId}` : ""}</Descriptions.Item>
            <Descriptions.Item label="Timing">{item.schedule.kind} · {item.schedule.timeZone}{item.schedule.localTime ? ` · ${item.schedule.localTime}` : ""}{item.schedule.kind === "fixedInterval" ? ` · every ${item.schedule.interval} seconds` : ""}</Descriptions.Item>
            {item.schedule.atUtc ? <Descriptions.Item label="Run at">{date(item.schedule.atUtc)}</Descriptions.Item> : null}
            {item.schedule.kind === "daily" || item.schedule.kind === "weekly" ? <Descriptions.Item label="Recurrence">Every {item.schedule.interval} {item.schedule.kind === "daily" ? "day" : "week"}{item.schedule.interval === 1 ? "" : "s"}{item.schedule.weekdays?.length ? ` · ${item.schedule.weekdays.map(day => ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"][day]).join(", ")}` : ""}</Descriptions.Item> : null}
            {item.schedule.endAtUtc || item.schedule.endDate ? <Descriptions.Item label="End">{item.schedule.endAtUtc ? date(item.schedule.endAtUtc) : item.schedule.endDate}</Descriptions.Item> : null}
            {item.schedule.maxOccurrences ? <Descriptions.Item label="Maximum occurrences">{item.schedule.maxOccurrences}</Descriptions.Item> : null}
            <Descriptions.Item label="Model">{item.effectiveModelKey ?? item.modelKey ?? "Unattended default"}</Descriptions.Item>
            <Descriptions.Item label="Latest execution">{item.executionStatus ?? "Not yet"}</Descriptions.Item></Descriptions>
            <Flex wrap gap={token.paddingXS}><Button aria-label="Run schedule now" aria-busy={item.registrationId in pending} loading={item.registrationId in pending}
              disabled={busy || !item.enabled || item.registrationId in pending || activeWork.includes(item.executionStatus ?? "")}
              onClick={() => void mutate(`schedules/${item.registrationId}/run`, { expectedRevision: item.revision })}>{item.registrationId in pending ? "Starting…" : "Run now"}</Button>
              <Button disabled={busy || terminal.includes(item.status)} onClick={() => edit(item)}>Edit schedule</Button>
              <Button disabled={busy || terminal.includes(item.status)} onClick={() => void mutate(`schedules/${item.registrationId}`, {
                expectedRevision: item.revision, enabled: !item.enabled, intent: item.intent, schedule: item.schedule, modelKey: item.modelKey, reasoningEffort: item.reasoningEffort
              }, "PUT")}>{item.enabled ? "Disable schedule" : "Enable schedule"}</Button>
              <Button danger disabled={busy || terminal.includes(item.status)} onClick={() => confirmAction(modal, { title: "Cancel this schedule?", content: "Stops future runs. Already admitted background work remains available there.", okText: "Cancel schedule", danger: true,
                onOk: () => mutate(`schedules/${item.registrationId}/cancel`, { expectedRevision: item.revision }) })}>Cancel schedule</Button>
              {item.lastWorkItemId ? <Button onClick={onWork}>Inspect scheduled execution</Button> : null}</Flex>
            </Flex>
          }} />
        </> : null}
    </Flex></div>
  </section>;
}

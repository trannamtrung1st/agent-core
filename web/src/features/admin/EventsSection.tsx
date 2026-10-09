import { useCallback, useEffect, useId, useRef, useState } from "react";
import { Alert, App, Button, Descriptions, Drawer, Dropdown, Empty, Flex, Form, Grid, Input, Modal, Spin, Table, Tag, Typography, theme } from "antd";
import { MoreOutlined, PlusOutlined } from "@ant-design/icons";
import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";
import { confirmAction } from "../../app/confirmAction";
import { adminHomePath, adminInstancePath, navigateToAppPath } from "../../app/appRoute";
import { createWebhookEvent, getWebhookEvent, listWebhookEvents, renameWebhookEvent, revokeWebhookEvent, rotateWebhookEvent,
  type AdminWebhookEvent, type AdminWebhookEventDetails } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminErrorNotice, AdminRetryAction } from "./adminFailure";
import { useAdminDetailLayout } from "./useAdminDetailLayout";
import { WebhookRequestExample } from "./WebhookRequestExample";

export const webhookUrl = (eventKey: string) => `${window.location.origin}/api/v1/hooks/${encodeURIComponent(eventKey)}`;
const timestamp = (value: string | null) => value ? new Date(value).toLocaleString() : "Not received yet";
export const validEventKey = (value: string) => /^[a-z](?:[a-z0-9._-]{0,62}[a-z0-9])?$/.test(value);

export function EventsSection({ onChanged, onCreated, embedded = false }: {
  onChanged?: (events: AdminWebhookEvent[]) => void; onCreated?: (eventId: string) => void; embedded?: boolean;
} = {}) {
  const { token } = theme.useToken(); const { message, modal } = App.useApp();
  const compact = !Grid.useBreakpoint().md; const detailLayout = useAdminDetailLayout();
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const [events, setEvents] = useState<AdminWebhookEvent[]>([]);
  const [tableVersion, setTableVersion] = useState(0);
  const [loading, setLoading] = useState(true); const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState<AdminFailureNotice | null>(null); const [busy, setBusy] = useState(false);
  const [editor, setEditor] = useState<"new" | AdminWebhookEvent | null>(null);
  const [name, setName] = useState(""); const [key, setKey] = useState("");
  const [editorError, setEditorError] = useState<AdminFailureNotice | null>(null);
  const [detailId, setDetailId] = useState<string | null>(() => embedded ? null : new URLSearchParams(window.location.search).get("event"));
  const [details, setDetails] = useState<AdminWebhookEventDetails | null>(null);
  const [detailError, setDetailError] = useState<AdminFailureNotice | null>(null);
  const [detailLoading, setDetailLoading] = useState(false); const [detailRead, setDetailRead] = useState(0);
  const [credential, setCredential] = useState<string | null>(null); const [copyError, setCopyError] = useState<string | null>(null);
  const formId = useId(); const opener = useRef<HTMLElement | null>(null); const newButton = useRef<HTMLButtonElement>(null);
  const nameInput = useRef<import("antd").InputRef>(null); const pendingCredential = useRef<string | null>(null);
  const generation = useRef(0); const changed = useRef(onChanged); changed.current = onChanged;
  const reload = useCallback(async () => {
    const version = ++generation.current; setLoading(true); setError(null);
    try { const rows = await listWebhookEvents(); if (version === generation.current) { setEvents(rows); setLoaded(true); changed.current?.(rows); } }
    catch (reason) { if (version === generation.current) setError(describeAdminError(reason, "Events could not be loaded.")); }
    finally { if (version === generation.current) setLoading(false); }
  }, []);
  useEffect(() => { void reload(); return () => { generation.current++; }; }, [reload]);
  useEffect(() => {
    if (!detailId) return; let current = true; setDetailLoading(true); setDetails(null); setDetailError(null);
    void getWebhookEvent(detailId).then(result => { if (current) setDetails(result); })
      .catch(reason => { if (current) setDetailError(describeAdminError(reason, "Event details could not be loaded.")); })
      .finally(() => { if (current) setDetailLoading(false); });
    return () => { current = false; };
  }, [detailId, detailRead]);
  useEffect(() => {
    if (embedded) return;
    const syncSelection = () => setDetailId(window.location.pathname === adminHomePath("events") ? new URLSearchParams(window.location.search).get("event") : null);
    window.addEventListener("popstate", syncSelection);
    return () => window.removeEventListener("popstate", syncSelection);
  }, [embedded]);
  const openDetails = (id: string) => {
    setDetailId(id);
    if (!embedded) navigateToAppPath(`${adminHomePath("events")}?event=${id}`);
  };
  const closeDetails = () => {
    setDetailId(null);
    if (!embedded && new URLSearchParams(window.location.search).has("event")) navigateToAppPath(adminHomePath("events"), true);
  };
  const restoreFocus = () => (opener.current?.isConnected ? opener.current : newButton.current)?.focus();
  async function copy(value: string) {
    try { await navigator.clipboard.writeText(value); setCopyError(null); void message.success("Copied."); }
    catch { setCopyError("Copy failed. Select the value and copy it manually."); }
  }
  const openEditor = (value: "new" | AdminWebhookEvent, trigger: HTMLElement | null = document.activeElement as HTMLElement) => {
    opener.current = trigger; setName(value === "new" ? "" : value.displayName);
    setKey(value === "new" ? "" : value.eventKey); setEditorError(null); setEditor(value);
  };
  async function save() {
    if (!editor || busy) return; setBusy(true); setEditorError(null);
    try {
      if (editor === "new") {
        const saved = await createWebhookEvent(name.trim(), key); pendingCredential.current = saved.token; onCreated?.(saved.eventId); setSearch(""); setTableVersion(v => v + 1);
      } else { await renameWebhookEvent(editor.eventId, name.trim(), editor.revision); setDetailRead(v => v + 1); }
      setEditor(null); await reload();
    } catch (reason) { setEditorError(describeAdminError(reason, "Event could not be saved. Your input is retained.")); }
    finally { setBusy(false); }
  }
  async function rotate(item: AdminWebhookEvent) {
    setBusy(true);
    try { const saved = await rotateWebhookEvent(item.eventId); setCredential(saved.token); await reload(); setDetailRead(v => v + 1); }
    catch (reason) { setError(describeAdminError(reason, item.status === "Revoked" ? "Event reactivation failed. Retry after refreshing." : "Credential rotation failed. Retry after refreshing.")); }
    finally { setBusy(false); }
  }
  async function revoke(item: AdminWebhookEvent) {
    setBusy(true);
    try { await revokeWebhookEvent(item.eventId); await reload(); setDetailRead(v => v + 1); }
    catch (reason) { setError(describeAdminError(reason, "Event revocation failed. Retry after refreshing.")); }
    finally { setBusy(false); }
  }
  const credentialAction = (item: AdminWebhookEvent) => item.status === "Revoked" ? "Reactivate Event" : "Rotate credential";
  const confirmCredential = (item: AdminWebhookEvent) => confirmAction(modal, {
    title: item.status === "Revoked" ? "Reactivate this Event?" : "Rotate this credential?",
    content: item.status === "Revoked"
      ? `This Event will accept new signals again. Its ${item.activeSubscriberCount} active subscriber(s) can receive future signals. A new credential will be issued; update all webhook clients. Previously skipped deliveries are not replayed.`
      : "The current credential stops working immediately. All webhook clients must update their secret. Copy the new secret before leaving.",
    okText: credentialAction(item), danger: item.status !== "Revoked", onOk: () => rotate(item), afterClose: restoreFocus
  });
  const confirmRevoke = (item: AdminWebhookEvent) => confirmAction(modal, {
    title: "Revoke this Event?", content: `New signals and pending deliveries will be rejected. This Event has ${item.activeSubscriberCount} active subscriber(s) and ${item.subscriberCount} total subscription(s). Existing admitted Runs remain available.`,
    okText: "Revoke Event", danger: true, onOk: () => revoke(item), afterClose: restoreFocus
  });
  const actions = (item: AdminWebhookEvent, detail = false) => detail ? <Flex wrap gap={token.paddingXS} align="center">
    <Button size="small" disabled={busy} onClick={() => openEditor(item)}>Rename</Button>
    <Button size="small" disabled={busy} aria-label={`${credentialAction(item)} for ${item.displayName}`} onClick={e => { opener.current = e.currentTarget; confirmCredential(item); }}>{credentialAction(item)}</Button>
    {item.status === "Active" ? <Button size="small" danger aria-label={`Revoke ${item.displayName}`} disabled={busy} onClick={e => { opener.current = e.currentTarget; confirmRevoke(item); }}>Revoke Event</Button> : null}
  </Flex> : <Flex className="admin-table-actions" gap={token.paddingXS} align="center">
    <Button size="small" onClick={e => { opener.current = e.currentTarget; openDetails(item.eventId); }} aria-label={`View details for ${item.displayName}`}>View</Button>
    <Dropdown trigger={["click"]} menu={{ items: [
      { key: "rename", label: "Rename", disabled: busy },
      { key: "credential", label: credentialAction(item), disabled: busy },
      ...(item.status === "Active" ? [{ key: "revoke", label: "Revoke Event", danger: true, disabled: busy }] : [])
    ], onClick: ({ key }) => { if (key === "rename") openEditor(item, opener.current); else if (key === "credential") confirmCredential(item); else confirmRevoke(item); } }}>
      <Button size="small" icon={<MoreOutlined aria-hidden />} aria-label={`More actions for ${item.displayName}`} disabled={busy} onClick={e => { opener.current = e.currentTarget; }} />
    </Dropdown>
  </Flex>;
  const navigateSubscription = (instanceId: string, automationId: string) => navigateToAppPath(`${adminInstancePath(instanceId, "automation", "automations")}?automation=${automationId}`);
  const nameError = /[\u0000-\u001f\u007f-\u009f]/.test(name) ? "Use a name without control characters." : null;
  return <section className="admin-definition-panel" aria-label="Events">
    <div className="admin-definition-panel-heading"><Typography.Title level={4}>Events</Typography.Title>
      <Typography.Text type="secondary">Receive external signals through authenticated webhooks. Automations choose how each agent responds.</Typography.Text></div>
    <div className="admin-definition-panel-body"><Flex vertical gap={token.padding}>
      {loading ? <Spin aria-label="Loading Events" /> : null}
      {error ? <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} />} action={<AdminRetryAction onRetry={() => void reload()} />} /> : null}
      <Flex wrap gap={token.paddingSM} align="center" justify="space-between">
        <div style={{ flex: "1 1 18rem", minWidth: 0 }}><AdminCollectionToolbar label="events" value={search} onChange={setSearch} /></div>
        <Flex gap={token.paddingXS}><Button ref={newButton} type="primary" icon={<PlusOutlined aria-hidden />} disabled={busy} onClick={() => openEditor("new")}>New Event</Button>
          <Button disabled={busy || loading} onClick={() => void reload()}>Refresh Events</Button></Flex>
      </Flex>
      {loaded ? <Table<AdminWebhookEvent> key={tableVersion} aria-label="Events table" className="admin-collection-table" rowKey="eventId" size="small"
        dataSource={events.filter(e => [e.displayName, e.eventKey, e.status].some(value => value.toLowerCase().includes(search.trim().toLowerCase())))}
        pagination={pagination} scroll={{ x: 1040 }} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search || events.length ? "No matches. Clear search or filters to see all results." : "No Events yet. Create an Event, then subscribe an Automation to it."} /> }} columns={[
          { title: "Name", dataIndex: "displayName", width: 220, ellipsis: true, sorter: (a,b) => a.displayName.localeCompare(b.displayName), render: (_, item) => <Button type="link" size="small" className="admin-collection-name" title={item.displayName} onClick={e => { opener.current = e.currentTarget; openDetails(item.eventId); }}>{item.displayName}</Button> },
          { title: "Key", dataIndex: "eventKey", width: 180, ellipsis: true },
          { title: "Status", dataIndex: "status", width: 100, filters: ["Active","Revoked"].map(value => ({ text: value, value })), onFilter: (value,item) => item.status === value, render: value => <Tag color={value === "Active" ? "success" : "default"}>{value}</Tag> },
          { title: "Active subscribers", dataIndex: "activeSubscriberCount", width: 110, align: "right", sorter: (a,b) => a.activeSubscriberCount - b.activeSubscriberCount },
          { title: "Total subscriptions", dataIndex: "subscriberCount", width: 120, align: "right", sorter: (a,b) => a.subscriberCount - b.subscriberCount },
          { title: "Last received", dataIndex: "lastReceivedAt", width: 180, render: timestamp },
          { title: "Actions", width: 130, render: (_,item) => actions(item) }
        ]} /> : null}
      <Drawer open={!!editor} title={editor === "new" ? "New Event" : "Edit Event name"} size={compact ? "100%" : 480}
        getContainer={false} rootStyle={{ position: "fixed" }} rootClassName="admin-automation-drawer"
        styles={{ body: { padding: token.padding }, footer: { padding: token.padding } }}
        closable={!busy} maskClosable={!busy} keyboard={!busy} focusable={{ trap: !!editor, focusTriggerAfterClose: false }}
        onClose={() => setEditor(null)} afterOpenChange={open => {
          if (open) nameInput.current?.focus();
          else if (pendingCredential.current) { setCredential(pendingCredential.current); pendingCredential.current = null; }
          else restoreFocus();
        }} footer={<Flex justify="flex-end" gap={token.paddingXS}><Button disabled={busy} onClick={() => setEditor(null)}>Cancel</Button>
          <Button type="primary" aria-label={editor === "new" ? "Create Event" : "Save name"} htmlType="submit" form={formId} loading={busy} aria-busy={busy} disabled={busy || !name.trim() || !!nameError || editor === "new" && !validEventKey(key)}>{editor === "new" ? "Create Event" : "Save name"}</Button></Flex>}>
        {editorError ? <Alert type="error" showIcon title={<AdminErrorNotice message={editorError.message} diagnosticId={editorError.diagnosticId} />} /> : null}
        <Form id={formId} layout="vertical" className="admin-config-form" onFinish={() => void save()}>
          <Form.Item label="Event name" help={nameError} validateStatus={nameError ? "error" : undefined}><Input ref={nameInput} aria-label="Event name" maxLength={80} value={name} disabled={busy} onChange={e => setName(e.target.value)} /></Form.Item>
          <Form.Item label="Event key" extra="Immutable identifier for the webhook URL. Use lowercase letters, digits, dots, hyphens or underscores; start with a letter and end with a letter or digit. The secret authenticates requests."
            validateStatus={key && !validEventKey(key) ? "error" : undefined} help={key && !validEventKey(key) ? "Enter a valid key, up to 64 characters." : undefined}>
            <Input aria-label="Event key" maxLength={64} placeholder="order.placed" value={key} disabled={busy || editor !== "new"} onChange={e => setKey(e.target.value)} /></Form.Item>
        </Form>
      </Drawer>
      <Drawer open={!!detailId && !editor && !credential} title={details?.event.displayName ?? "Event details"} size={compact ? "100%" : 640}
        getContainer={false} rootStyle={{ position: "fixed" }} rootClassName="admin-automation-drawer" styles={{ body: { padding: token.padding } }}
        closable={!busy} maskClosable={!busy} keyboard={!busy}
        onClose={closeDetails} afterOpenChange={open => { if (!open && !editor && !credential) restoreFocus(); }}>
        <Flex vertical gap={token.padding}>
          {detailLoading ? <Spin aria-label="Loading Event details" /> : null}
          {detailError ? <Alert type="error" showIcon title={detailError.message} action={<AdminRetryAction onRetry={() => setDetailRead(v => v + 1)} />} /> : null}
          {details ? <>
            <Descriptions bordered column={1} size="small" {...detailLayout}>
              <Descriptions.Item label="Event key">{details.event.eventKey}</Descriptions.Item>
              <Descriptions.Item label="Event ID">{details.event.eventId}</Descriptions.Item>
              <Descriptions.Item label="Webhook URL"><Typography.Text copyable={{ text: webhookUrl(details.event.eventKey) }}>{webhookUrl(details.event.eventKey)}</Typography.Text></Descriptions.Item>
              <Descriptions.Item label="Status">{details.event.status}</Descriptions.Item>
              <Descriptions.Item label="Active subscribers">{details.event.activeSubscriberCount}</Descriptions.Item>
              <Descriptions.Item label="Total subscriptions">{details.event.subscriberCount}</Descriptions.Item>
              <Descriptions.Item label="Created">{timestamp(details.event.createdAt)}</Descriptions.Item>
              <Descriptions.Item label="Updated">{timestamp(details.event.updatedAt)}</Descriptions.Item>
              <Descriptions.Item label="Last received">{timestamp(details.event.lastReceivedAt)}</Descriptions.Item>
            </Descriptions>
            {actions(details.event, true)}
            <WebhookRequestExample url={webhookUrl(details.event.eventKey)} />
            <Flex component="section" vertical gap={token.paddingXS} aria-label="Subscribed Automations">
            <Typography.Title level={5} style={{ margin: 0 }}>Subscribed Automations</Typography.Title>
            <Typography.Text type="secondary">Active subscriptions can receive new signals while the Event is active. Disabled subscriptions remain in the total. Admission still depends on agent policy and availability.</Typography.Text>
            {details.subscribers.length ? <Table size="small" rowKey="automationId" className="admin-collection-table" scroll={{ x: 500 }} pagination={false} dataSource={details.subscribers} columns={[
              { title: "Automation", dataIndex: "name", render: (_,s) => <Button type="link" size="small" onClick={() => navigateSubscription(s.agentInstanceId,s.automationId)}>{s.name}</Button> },
              { title: "Status", dataIndex: "status" }
            ]} /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No subscriptions yet. Create an Event Automation on an Instance." />}
            </Flex>
            <Flex component="section" vertical gap={token.paddingXS} aria-label="Received signals">
            <Typography.Title level={5} style={{ margin: 0 }}>Received signals</Typography.Title>
            <Typography.Text type="secondary">The last 20 accepted signals, including signals with no active subscribers. Repeated delivery IDs are counted once; later subscribers do not receive earlier signals.</Typography.Text>
            {details.signals.length ? <Table size="small" rowKey="receiptId" className="admin-collection-table" scroll={{ x: 480 }} pagination={{ pageSize: 10 }} dataSource={details.signals} columns={[
              { title: "Delivery ID", dataIndex: "sourceEventId", ellipsis: true }, { title: "Received", dataIndex: "receivedAt", render: timestamp }
            ]} /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No signals received yet. Send a request using the example above." />}
            </Flex>
            <Flex component="section" vertical gap={token.paddingXS} aria-label="Automation deliveries">
            <Typography.Title level={5} style={{ margin: 0 }}>Automation deliveries</Typography.Title>
            <Typography.Text type="secondary">Per-subscriber admission for those signals. Admitted means durable work was created; execution results are available in the Automation’s Runs.</Typography.Text>
            {details.deliveries.length ? <Table size="small" rowKey={d => `${d.receiptId}:${d.automationId}`} className="admin-collection-table" scroll={{ x: 600 }} pagination={{ pageSize: 10 }} dataSource={details.deliveries} columns={[
              { title: "Delivery ID", dataIndex: "sourceEventId", ellipsis: true }, { title: "Received", dataIndex: "receivedAt", render: timestamp },
              { title: "Status", dataIndex: "status" }, { title: "Automation", render: (_,d) => <Button type="link" size="small" onClick={() => navigateSubscription(d.agentInstanceId,d.automationId)}>View Automation</Button> }
            ]} /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No Automation deliveries for recent signals. Only active subscriptions at receipt time can receive them." />}
            </Flex>
          </> : null}
        </Flex>
      </Drawer>
      <Modal open={credential !== null} title="Copy this credential" className="admin-credential-dialog" okText="Done" cancelButtonProps={{ style: { display: "none" } }} destroyOnHidden
        focusable={{ trap: credential !== null, focusTriggerAfterClose: false }} onOk={() => { setCredential(null); setCopyError(null); }} onCancel={() => { setCredential(null); setCopyError(null); }} afterClose={restoreFocus}>
        <Flex vertical gap={token.paddingXS}><Typography.Paragraph>Copy this secret now. It authenticates webhook clients and will not be shown again. It grants no agent capabilities or system credential access.</Typography.Paragraph>
          {copyError ? <Alert type="error" showIcon title={copyError} /> : null}
          <Input.TextArea readOnly aria-label="Event credential" value={credential ?? ""} rows={3} /><Button onClick={() => void copy(credential ?? "")}>Copy credential</Button>
        </Flex>
      </Modal>
    </Flex></div>
  </section>;
}

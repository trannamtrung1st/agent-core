import { useEffect, useRef, useState } from "react";
import { Alert, Button, Flex, Form, Input, Select, Typography, theme } from "antd";
import { getSession, listCatalog, type CatalogItem } from "../../services/api";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminRetryAction } from "./adminFailure";

/** Owner conversation browsing shared by session-scoped Memory and Experience. */
export function AdminSessionPicker({ instanceId, value, onChange, disabled = false, eligibleOnly = false, label = "Source conversation" }: {
  instanceId: string;
  value: string;
  onChange: (sessionId: string) => void;
  disabled?: boolean;
  eligibleOnly?: boolean;
  label?: string;
}) {
  const { token } = theme.useToken();
  const [manual, setManual] = useState(false);
  const [items, setItems] = useState<CatalogItem[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const order = useRef(0);
  const pending = useRef(false);
  useEffect(() => {
    order.current++; pending.current = false;
    setItems([]); setCursor(null); setLoaded(false); setLoading(false); setError(null);
    return () => { order.current++; };
  }, [instanceId]);

  async function load(more = false) {
    if (pending.current) return;
    pending.current = true;
    const generation = ++order.current;
    setLoading(true); setError(null);
    try {
      const page = await listCatalog({ limit: 50, includeArchived: true, cursor: more ? cursor : null });
      if (generation !== order.current) return;
      setItems(current => more ? [...current, ...page.items.filter(item => !current.some(row => row.sessionId === item.sessionId))] : page.items);
      setCursor(page.hasMore ? page.nextCursor : null); setLoaded(true);
    } catch (reason) {
      if (generation === order.current) setError(describeAdminError(reason, "Conversations could not be loaded. Retry or enter a Session ID."));
    } finally {
      if (generation === order.current) { pending.current = false; setLoading(false); }
    }
  }

  async function choose(sessionId: string | undefined) {
    if (pending.current) return;
    if (!sessionId) { onChange(""); return; }
    pending.current = true;
    const generation = ++order.current;
    setLoading(true); setError(null);
    try {
      const session = await getSession(sessionId);
      if (generation !== order.current) return;
      if (eligibleOnly && (["ended", "ending"].includes(session.status.toLowerCase()) || ["completed", "cancelled", "expired", "ended"].includes((session.lifecycleStatus ?? "").toLowerCase())
        || session.status.toLowerCase() === "paused" && !["disconnected", "recovered"].includes(session.pauseReason ?? ""))) {
        setError({ message: "Choose an active conversation. Paused, ended and archived conversations cannot receive scheduled work." }); return;
      }
      if (session.agentInstanceId !== instanceId) {
        setError({ message: "This conversation belongs to another instance. Choose a conversation for this instance or enter its Session ID." });
        return;
      }
      onChange(sessionId);
    } catch (reason) {
      if (generation === order.current) setError(describeAdminError(reason, "This conversation could not be checked. Retry or enter its Session ID."));
    } finally { if (generation === order.current) { pending.current = false; setLoading(false); } }
  }

  const labels = items.filter(item => !eligibleOnly || (item.agentInstanceId === instanceId && !item.archived && !item.ended
    && !["completed", "cancelled", "expired", "ended"].includes((item.lifecycleStatus ?? "").toLowerCase())
    && (item.status.toLowerCase() !== "paused" || ["disconnected", "recovered"].includes(item.pauseReason ?? "")))).map(item => {
    const timestamp = new Date(item.updatedAt);
    const date = Number.isFinite(timestamp.getTime()) ? timestamp.toLocaleString() : "Date unavailable";
    const label = `${item.title || item.agentName || "Untitled conversation"} · ${date}${eligibleOnly ? ` · ${item.sessionId.slice(0, 8)}` : ""}`;
    return { value: item.sessionId, label, title: `${label} · ${item.sessionId}`,
      searchText: `${label} ${item.agentName ?? ""} ${item.agentId} ${item.sessionId}` };
  });
  return <Flex vertical gap={token.paddingXS} className="admin-session-picker">
    {manual ? <Form.Item label="Session ID" style={{ marginBottom: 0 }}>
      <Input aria-label="Session ID" value={value} disabled={disabled}
        placeholder="Paste a conversation Session ID" onChange={event => onChange(event.target.value)} />
    </Form.Item> : <Select aria-label={label} showSearch allowClear value={value || null}
      placeholder="Choose a conversation by name or date" loading={loading} disabled={disabled}
      options={labels} optionFilterProp="searchText" style={{ width: "100%" }}
      onOpenChange={open => { if (open && !loaded) void load(); }} onChange={id => void choose(id)}
      notFoundContent={loading ? "Loading conversations…" : cursor
        ? "No matching conversations. Load older conversations or enter a Session ID."
        : "No matching conversations. Start one in Chat or enter a Session ID."} />}
    <Flex wrap gap={token.paddingXS}>
      <Button type="link" size="small" disabled={disabled} onClick={() => { order.current++; pending.current = false; setLoading(false); setManual(current => !current); setError(null); }}>
        {manual ? "Browse conversations" : "Enter Session ID"}
      </Button>
      {!manual && loaded ? <Button size="small" disabled={disabled || loading} onClick={() => void load()}>Refresh conversations</Button> : null}
      {!manual && cursor ? <Button size="small" disabled={disabled || loading} onClick={() => void load(true)}>Load older conversations</Button> : null}
    </Flex>
    {error ? <Alert type="error" showIcon title={error.message}
      action={<AdminRetryAction onRetry={() => void load()} diagnosticId={error.diagnosticId} />} /> : null}
    {!manual && value ? <Typography.Text type="secondary" style={{ overflowWrap: "anywhere" }}>Session ID: {value}</Typography.Text> : null}
  </Flex>;
}

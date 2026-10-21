import { Alert, Button, Collapse, Drawer, Empty, Flex, Spin, Table, Tag, Typography, theme } from "antd";
import { useEffect, useRef, useState } from "react";
import { followAppLink } from "../../app/appLink";
import { navigateToAppPath } from "../../app/appRoute";
import { sessionPath } from "../../app/sessionRoute";
import { type ActivitySource } from "../../services/activitySource";
import { type InstanceActivitySession } from "../../services/api";
import { tenantAgentPath } from '../access/accessRoute';
import { SessionRunHistory } from "../chat/AgentRunDetails";
import { BackgroundWorkDrawer } from "../chat/BackgroundWorkDrawer";
import { formatChatTime } from "../chat/chatTime";
import { DrawerListFooter } from "../chat/DrawerListFooter";
import { runOriginLabel } from "../chat/runPresentation";
import { SessionArtifacts } from "../chat/SessionArtifacts";
import { useCursorPages } from "../chat/useCursorPages";
import { AdminCollectionToolbar } from "./AdminCollectionToolbar";
import { useActivitySearch } from "./useActivitySearch";

export function activitySessionTitle(row: InstanceActivitySession) {
  return row.session.title?.trim() || (row.origin === "UserChat" ? "Untitled conversation" : "Untitled background task");
}

export function InstanceSessionsSection({ instanceId, open, archived = false, source, activityPath }: { instanceId: string; open: boolean; archived?: boolean; source: ActivitySource; activityPath?: string }) {
  const { token } = theme.useToken();
  const page = useCursorPages(`${source.scope}:${instanceId}`, open, (_scope, cursor) => source.listSessions(instanceId, cursor));
  const { search, setSearch } = useActivitySearch();
  const readSelection = () => new URLSearchParams(window.location.search).get("session");
  const [selectedId, setSelectedId] = useState(readSelection);
  const opener = useRef<HTMLElement | null>(null);
  useEffect(() => { const sync = () => setSelectedId(readSelection()); window.addEventListener("popstate", sync); return () => window.removeEventListener("popstate", sync); }, []);
  const [detail, setDetail] = useState<InstanceActivitySession | null>(null);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let current = true; setDetail(null); setDetailError(null);
    if (open && selectedId) void source.getSession(instanceId, selectedId).then(row => { if (current) setDetail(row); })
      .catch(reason => { if (current) setDetailError(reason instanceof Error ? reason.message : "Unable to load this session."); });
    return () => { current = false; };
  }, [instanceId, open, selectedId, retry, source]);
  // Catalog rows provide a title, but only the exact read authorizes current detail/actions.
  const selected = detail?.session.sessionId === selectedId ? detail : null;
  const selectedTitle = selected ?? page.items.find(row => row.session.sessionId === selectedId);
  const previousSelection = useRef<InstanceActivitySession | null>(null);
  if (selected) previousSelection.current = selected;
  // Keep the same drawer mounted until its closing animation restores initiating focus.
  const displayed = selected ?? (!selectedId ? previousSelection.current : null);
  const backPath = `${activityPath ?? tenantAgentPath(source.tenantId, instanceId, "activity-sessions")}${search ? `?${new URLSearchParams({ q: search })}` : ""}`;
  function inspect(row: InstanceActivitySession) {
    opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const params = new URLSearchParams(window.location.search); params.set("session", row.session.sessionId);
    navigateToAppPath(`${window.location.pathname}?${params}`);
  }
  function close() { navigateToAppPath(backPath, true); }
  const conversationHref = (id: string) => `${sessionPath(id, source.tenantId)}?${new URLSearchParams({ returnTo: backPath })}`;
  function openConversation(id: string) { navigateToAppPath(conversationHref(id)); }
  const sessionHref = (id: string) => `${backPath}${search ? "&" : "?"}${new URLSearchParams({ session: id })}`;
  const rows = [...new Map((page.error ? [] : page.items).map(row => [row.session.sessionId, row])).values()].filter(row => `${activitySessionTitle(row)} ${row.session.sessionId} ${runOriginLabel(row.origin)} ${row.session.lifecycleStatus ?? row.session.status}`.toLowerCase().includes(search.toLowerCase()));
  return <Flex vertical gap={token.padding} className="activity-collection">
    <Typography.Text type="secondary">Conversations and background work, most recently active first.</Typography.Text>
    <AdminCollectionToolbar label="loaded sessions" value={search} onChange={setSearch} />
    {page.error ? <Alert type="error" showIcon title={page.error} /> : null}
    <Table<InstanceActivitySession> aria-label="Activity sessions table" className="admin-collection-table" size="small"
      rowKey={row => row.session.sessionId} dataSource={rows} loading={page.loading} pagination={false} tableLayout="fixed" scroll={{ x: 800 }}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={page.error ? "Sessions could not be loaded. Retry below." : search ? "No matching loaded sessions. Clear search or load older sessions." : "No Sessions are available with your current access."} /> }}
      columns={[
        { title: "Session", key: "title", width: 320, render: (_, row) => <Flex vertical gap={token.paddingXS}>
          <Button type="link" className="admin-collection-name" title={activitySessionTitle(row)} href={sessionHref(row.session.sessionId)} onClick={event => followAppLink(event, () => inspect(row))}>{activitySessionTitle(row)}</Button>
          <Typography.Text type="secondary" className="admin-activity-id" copyable={{ text: row.session.sessionId }} title={row.session.sessionId}>ID {`${row.session.sessionId.slice(0, 8)}…${row.session.sessionId.slice(-8)}`}</Typography.Text>
        </Flex> },
        { title: "Origin", key: "origin", width: 160, render: (_, row) => runOriginLabel(row.origin) },
        { title: "Lifecycle", key: "status", width: 140, render: (_, row) => <Tag>{row.session.archived ? "Archived" : (row.session.lifecycleStatus ? row.session.lifecycleStatus[0].toUpperCase() + row.session.lifecycleStatus.slice(1) : row.session.ended ? "Ended" : "Unknown")}</Tag> },
        { title: "Last activity", key: "updated", width: 180, render: (_, row) => <time dateTime={row.session.updatedAt}>{formatChatTime(row.session.updatedAt) ?? "Unknown time"}</time> }
      ]} />
    <DrawerListFooter loadingMore={page.loadingMore} hasMore={page.hasMore} error={page.error} count={page.items.length} onLoadMore={() => void page.loadMore()} onRetry={() => void page.retry()} />
    {displayed?.surfaces.includes("BackgroundWork") ? <BackgroundWorkDrawer instanceId={instanceId} open={open && !!selectedId} wide initialSessionId={displayed.session.sessionId}
      source={source} conversationHref={conversationHref} onOpenConversation={openConversation} onClose={close} afterClose={() => opener.current?.isConnected && opener.current.focus()} /> :
      <Drawer title={selectedTitle ? activitySessionTitle(selectedTitle) : "Session details"} open={open && !!selectedId} onClose={close}
        size="min(640px, 100vw)" className="background-work-drawer" afterOpenChange={visible => { if (!visible && opener.current?.isConnected) opener.current.focus(); }}>
        {selected ? <Flex vertical gap={token.padding}>
          <Typography.Text type="secondary" copyable>{selected.session.sessionId}</Typography.Text>
          <Button style={{ alignSelf: "flex-start" }} disabled={archived || selected.session.archived} href={conversationHref(selected.session.sessionId)} onClick={event => followAppLink(event, () => openConversation(selected.session.sessionId))}>Open conversation</Button>
          {archived ? <Typography.Text type="secondary">Archived Agent Instance. Unarchive it from Identity &amp; version to open this conversation.</Typography.Text> : null}
          {selected.session.archived ? <Typography.Text type="secondary">Archived conversation. Its history and files remain available here.</Typography.Text> : null}
          <Collapse defaultActiveKey={["runs"]} items={[{ key: "runs", label: "Runs", children: <SessionRunHistory sessionId={selected.session.sessionId} open={open} source={source} /> }]} />
          <SessionArtifacts sessionId={selected.session.sessionId} tenantId={source.tenantId} open={open} />
        </Flex> : detailError ? <Alert type="error" showIcon title={detailError} action={<Button onClick={() => setRetry(value => value + 1)}>Retry session</Button>} /> : <Spin aria-label="Loading session details" />}
      </Drawer>}
  </Flex>;
}

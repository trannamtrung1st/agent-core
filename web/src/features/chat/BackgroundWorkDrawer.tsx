import { useEffect, useRef, useState } from "react";
import { Alert, Button, Drawer, Empty, Flex, Spin, Typography, theme } from "antd";
import { ArrowLeftOutlined, BellOutlined, MessageOutlined } from "@ant-design/icons";
import { continueInChat, getBackgroundSession, listBackgroundSessions, type BackgroundSession } from "../../services/api";
import { openCatalogSession } from "../../services/realtime";
import { refreshCatalog } from "../../services/catalog";
import { AgentRunStatus, SessionRunHistory } from "./AgentRunDetails";
import { DrawerListFooter } from "./DrawerListFooter";
import { useCursorPages } from "./useCursorPages";
import { useWorkReadState } from "./workReadState";
import { runOriginLabel } from "./runPresentation";
import { formatChatTime } from "./chatTime";
import { SessionArtifacts } from "./SessionArtifacts";
import { adminInstancePath, navigateToAppPath } from "../../app/appRoute";

export function BackgroundWorkDrawer({ instanceId, open, wide, onClose }: {
  instanceId: string; open: boolean; wide: boolean; onClose: () => void;
}) {
  const { token } = theme.useToken();
  const page = useCursorPages(instanceId, open, listBackgroundSessions);
  const [selected, setSelected] = useState<BackgroundSession | null>(null);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const epoch = useRef(0); const actionPending = useRef(false);
  const list = useRef<HTMLUListElement | null>(null);
  const backButton = useRef<HTMLButtonElement | HTMLAnchorElement | null>(null);
  const historyOpener = useRef<{ sessionId: string; control: "title" | "history" } | null>(null);
  const moveFocus = useRef(false);
  const { isUnread, markRead } = useWorkReadState();
  useEffect(() => {
    epoch.current++; setSelected(null); setBusy(false); setActionError(null); actionPending.current = false;
    historyOpener.current = null; moveFocus.current = false;
    return () => { epoch.current++; };
  }, [instanceId, open]);
  useEffect(() => {
    if (!open || !moveFocus.current) return;
    moveFocus.current = false;
    if (selected) { backButton.current?.focus(); return; }
    const origin = historyOpener.current;
    const row = Array.from(list.current?.querySelectorAll<HTMLElement>("[data-background-session-id]") ?? [])
      .find(item => item.dataset.backgroundSessionId === origin?.sessionId);
    const opener = row?.querySelector<HTMLButtonElement>(`[data-background-control="${origin?.control}"]`)
      ?? list.current?.querySelector<HTMLButtonElement>("[data-background-control]");
    opener?.focus({ preventScroll: true });
  }, [selected, open]);
  function select(item: BackgroundSession, control: "title" | "history") {
    historyOpener.current = { sessionId: item.session.sessionId, control }; moveFocus.current = true;
    setActionError(null); setSelected(item);
    if (item.latestRun && isUnread(item.latestRun)) {
      try { markRead([item.latestRun]); } catch (reason) { setActionError(reason instanceof Error ? reason.message : "Unable to mark this result as read."); }
    }
  }
  async function openChat(item: BackgroundSession) {
    if (actionPending.current) return;
    const generation = epoch.current; actionPending.current = true; setBusy(true); setActionError(null);
    try {
      const result = await continueInChat(item.session.sessionId);
      if (generation !== epoch.current) return;
      const current = await getBackgroundSession(result.sessionId);
      if (generation !== epoch.current) return;
      await refreshCatalog(true);
      if (generation !== epoch.current) return;
      const opened = await openCatalogSession(current.session);
      if (generation !== epoch.current) return;
      if (opened === "ready" || opened === "paused" || opened === "ended") onClose();
      else setActionError(opened === "blocked"
        ? "This Session is archived. Refresh to update its availability."
        : "The chat connection could not be opened. Try Continue in chat again.");
    } catch (reason) {
      if (generation === epoch.current) setActionError(reason instanceof Error ? reason.message : "Unable to open this Session. Refresh and try again.");
    } finally { if (generation === epoch.current) { actionPending.current = false; setBusy(false); } }
  }
  function automationLink(item: BackgroundSession) {
    if (!item.origin.automationId) return null;
    const path = `${adminInstancePath(instanceId, "automation", "automations")}?automation=${encodeURIComponent(item.origin.automationId)}`;
    return <Button href={path} onClick={event => { event.preventDefault(); navigateToAppPath(path); }}>View Automation</Button>;
  }
  function fileCount(item: BackgroundSession) {
    return `${item.artifactCount}${item.artifactCountHasMore ? "+" : ""} ${item.artifactCount === 1 && !item.artifactCountHasMore ? "file" : "files"}`;
  }
  const active = selected ? page.items.find(row => row.session.sessionId === selected.session.sessionId) ?? selected : null;
  return <Drawer title={active ? active.session.title : "Background work"} open={open} onClose={onClose}
    onKeyDown={event => {
      if (event.key !== "Escape") return;
      event.stopPropagation();
      onClose();
    }}
    size={wide ? "min(640px, 100vw)" : "100vw"} className="background-work-drawer">
    <Flex vertical gap={token.padding}>
      {active ? <>
        <Button ref={backButton} type="text" icon={<ArrowLeftOutlined aria-hidden />} style={{ alignSelf: "flex-start", paddingInline: token.paddingXS }} onClick={() => { moveFocus.current = true; setSelected(null); setActionError(null); }}>All background Sessions</Button>
        <Flex wrap align="center" gap={token.paddingXS}>
          <Typography.Text type="secondary">{runOriginLabel(active.origin.kind)}</Typography.Text>
          {active.origin.reportCompletion ? <Typography.Text type="secondary">Reports completion to its original chat</Typography.Text> : null}
        </Flex>
        <Button type="primary" icon={<MessageOutlined aria-hidden />} loading={busy} disabled={busy || !active.canContinueInChat} onClick={() => void openChat(active)}>Continue in chat</Button>
        {!active.canContinueInChat ? <Typography.Text type="secondary">This Session or its Agent Instance is unavailable for continuation. Its run history remains available here.</Typography.Text> : null}
        {automationLink(active)}
        <Typography.Text type="secondary">{fileCount(active)}</Typography.Text>
        <SessionRunHistory sessionId={active.session.sessionId} open={open} />
        <SessionArtifacts sessionId={active.session.sessionId} open={open} />
      </> : <>
        <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>Tasks run in their own Sessions. Open a result or continue the same conversation in chat.</Typography.Paragraph>
        {page.loading ? <Spin aria-label="Loading background Sessions" /> : page.items.length === 0 && !page.error ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No background Sessions yet" /> : null}
        <ul ref={list} className="background-work-list" aria-label="Background Sessions">
          {page.items.map(item => <li className="background-work-item" key={item.session.sessionId} data-background-session-id={item.session.sessionId}>
            <Flex vertical gap={token.paddingXS}>
              <Flex wrap align="center" justify="space-between" gap={token.paddingXS}>
                <Button type="text" className="background-work-item-title" data-background-control="title" onClick={() => select(item, "title")}>{item.session.title}</Button>
                {item.latestRun ? <AgentRunStatus run={item.latestRun} /> : null}
              </Flex>
              <Typography.Text type="secondary">{runOriginLabel(item.origin.kind)} · <time dateTime={item.session.updatedAt}>{formatChatTime(item.session.updatedAt) ?? "Unknown time"}</time></Typography.Text>
              <Typography.Text type="secondary">{fileCount(item)}{item.latestRun && item.latestRun.attemptCount > 1 ? ` · Attempt ${item.latestRun.attemptCount} of ${item.latestRun.maxAttempts}` : ""}</Typography.Text>
              {item.latestRun?.outcome?.summary || item.latestRun?.failureSummary ? <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }} ellipsis={{ rows: 2 }}>{item.latestRun.outcome?.summary || item.latestRun.failureSummary}</Typography.Paragraph> : null}
              {item.latestRun?.progress ? <Typography.Text>{item.latestRun.progress}</Typography.Text> : null}
              {item.latestRun && isUnread(item.latestRun) ? <Typography.Text><BellOutlined /> Unread · needs attention</Typography.Text> : null}
              <Flex wrap gap={token.paddingXS}><Button data-background-control="history" onClick={() => select(item, "history")}>View history</Button>
                <Button disabled={busy || !item.canContinueInChat} onClick={() => void openChat(item)}>Continue in chat</Button>{automationLink(item)}</Flex>
            </Flex>
          </li>)}
        </ul>
        {page.error ? <Alert type="error" showIcon title={page.error} /> : null}
        <DrawerListFooter loadingMore={page.loadingMore} hasMore={page.hasMore} error={page.error} count={page.items.length} onLoadMore={() => void page.loadMore()} onRetry={() => void page.retry()} />
      </>}
      {actionError ? <Alert type="error" showIcon title={actionError} action={<Button onClick={() => void page.refresh()}>Refresh</Button>} /> : null}
    </Flex>
  </Drawer>;
}

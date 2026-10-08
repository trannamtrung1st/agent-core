import { CompletionDeliveryStatus } from "./AutomationDestination";
import { useEffect, useRef, useState } from "react";
import { Alert, Button, Collapse, Drawer, Empty, Flex, Spin, Typography, theme } from "antd";
import { ArrowLeftOutlined, BellOutlined, CheckOutlined, MessageOutlined } from "@ant-design/icons";
import { getInstanceAgentRun, type AgentRun, continueInChat, getBackgroundSession, listBackgroundSessions, type BackgroundSession } from "../../services/api";
import { openCatalogSession } from "../../services/realtime";
import { refreshCatalog } from "../../services/catalog";
import { AgentRunDetailDrawer, AgentRunDetails, AgentRunStatus, SessionRunHistory } from "./AgentRunDetails";
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
  const [handlingRun, setHandlingRun] = useState<AgentRun | null>(null);
  const [handlingOpen, setHandlingOpen] = useState(false);
  const [selected, setSelected] = useState<BackgroundSession | null>(null);
  const [busy, setBusy] = useState(false);
  const [markingRead, setMarkingRead] = useState(false);
  const [readFeedback, setReadFeedback] = useState<{ kind: "error" | "success"; message: string } | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const epoch = useRef(0); const actionPending = useRef(false);
  const list = useRef<HTMLUListElement | null>(null);
  const backButton = useRef<HTMLButtonElement | HTMLAnchorElement | null>(null);
  const historyOpener = useRef<{ sessionId: string; control: "history" } | null>(null);
  const moveFocus = useRef(false);
  const { isUnread, markRead } = useWorkReadState();
  useEffect(() => {
    epoch.current++; setSelected(null); setHandlingOpen(false); setHandlingRun(null); setBusy(false); setMarkingRead(false); setActionError(null); actionPending.current = false;
    historyOpener.current = null; moveFocus.current = false;
    setReadFeedback(null);
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
  async function inspectHandlingRun(runId: string) {
    const generation = epoch.current; setHandlingRun(null); setHandlingOpen(true);
    try { const run = await getInstanceAgentRun(instanceId, runId); if (generation === epoch.current) setHandlingRun(run); }
    catch (reason) { if (generation === epoch.current) { setHandlingOpen(false); setActionError(reason instanceof Error ? reason.message : "Unable to load run. Try again."); } }
  }
  function select(item: BackgroundSession, control: "history") {
    historyOpener.current = { sessionId: item.session.sessionId, control }; moveFocus.current = true;
    setActionError(null); setSelected(item);
    if (item.initialRun && isUnread(item.initialRun)) {
      try { markRead([item.initialRun]); } catch (reason) { setActionError(reason instanceof Error ? reason.message : "Unable to mark this result as read."); }
    }
  }
  async function openChat(item: BackgroundSession) {
    if (actionPending.current) return;
    const generation = epoch.current; actionPending.current = true; setBusy(true); setActionError(null);
    try {
      const result = item.surfaces.includes("ChatList")
        ? { sessionId: item.session.sessionId } : await continueInChat(item.session.sessionId);
      if (generation !== epoch.current) return;
      page.replace(rows => rows.map(row => row.session.sessionId === result.sessionId
        ? { ...row, surfaces: Array.from(new Set([...row.surfaces, "ChatList"])) } : row));
      const current = await getBackgroundSession(result.sessionId);
      if (generation !== epoch.current) return;
      page.replace(rows => rows.map(row => row.session.sessionId === current.session.sessionId ? current : row));
      await refreshCatalog(true);
      if (generation !== epoch.current) return;
      const opened = await openCatalogSession(current.session);
      if (generation !== epoch.current) return;
      if (opened === "ready" || opened === "paused" || opened === "ended") onClose();
      else setActionError(opened === "blocked"
        ? "This Session is archived. Refresh to update its availability."
        : "The chat connection could not be opened. Try opening chat again.");
    } catch (reason) {
      if (generation === epoch.current) setActionError(reason instanceof Error ? reason.message : "Unable to open this Session. Refresh and try again.");
    } finally { if (generation === epoch.current) { actionPending.current = false; setBusy(false); } }
  }
  function automationLink(item: BackgroundSession) {
    if (!item.origin.automationId) return null;
    const path = `${adminInstancePath(instanceId, "automation", "automations")}?automation=${encodeURIComponent(item.origin.automationId)}`;
    return <Typography.Link href={path} onClick={event => { event.preventDefault(); navigateToAppPath(path); }}>View Automation</Typography.Link>;
  }
  async function markAllRead() {
    if (actionPending.current) return;
    const generation = epoch.current;
    actionPending.current = true; setBusy(true); setMarkingRead(true); setReadFeedback(null);
    try {
      const runs: AgentRun[] = [];
      const cursors = new Set<string>();
      let cursor: string | undefined;
      do {
        const result = await listBackgroundSessions(instanceId, cursor, 100);
        if (generation !== epoch.current) return;
        for (const item of result.items) {
          if (item.initialRun && item.completionDelivery?.status !== "handled") runs.push(item.initialRun);
        }
        if (!result.hasMore) break;
        if (!result.nextCursor || cursors.has(result.nextCursor)) throw new Error("Unable to load all background results. Try marking all as read again.");
        cursor = result.nextCursor; cursors.add(cursor);
      } while (true);
      // Commit once every page succeeds; a failed page leaves unread results intact.
      markRead(runs);
      setReadFeedback({ kind: "success", message: "Read status saved in this browser." });
    } catch (reason) {
      if (generation === epoch.current) setReadFeedback({ kind: "error", message: reason instanceof Error ? reason.message : "Unable to mark all results as read. Try again." });
    } finally {
      if (generation === epoch.current) { actionPending.current = false; setBusy(false); setMarkingRead(false); }
    }
  }
  function fileCount(item: BackgroundSession) {
    return `${item.artifactCount}${item.artifactCountHasMore ? "+" : ""} ${item.artifactCount === 1 && !item.artifactCountHasMore ? "file" : "files"}`;
  }
  const active = selected ? page.items.find(row => row.session.sessionId === selected.session.sessionId) ?? selected : null;
  return <><Drawer title={active ? active.originalTitle ?? "Background task" : "Background work"} open={open} onClose={onClose}
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
          {active.completionDelivery ? <CompletionDeliveryStatus delivery={active.completionDelivery} onInspect={id => void inspectHandlingRun(id)} /> : null}
        </Flex>
        <Button type="primary" icon={<MessageOutlined aria-hidden />} loading={busy} disabled={busy || !active.canContinueInChat} onClick={() => void openChat(active)}>{active.surfaces.includes("ChatList") ? "Open chat" : "Continue in chat"}</Button>
        {!active.canContinueInChat ? <Typography.Text type="secondary">This Session or its Agent Instance is unavailable for continuation. Its run history remains available here.</Typography.Text> : null}
        {active.surfaces.includes("ChatList") ? <Typography.Text type="secondary">Continued in chat</Typography.Text> : null}
        {automationLink(active)}
        <Typography.Text type="secondary">{fileCount(active)}</Typography.Text>
        {active.initialRun ? <AgentRunDetails run={active.initialRun} onChange={() => void page.refresh()} />
          : <Alert type="info" title="The original run is unavailable." />}
        <Collapse items={[{ key: "history", label: "Conversation run history", children: <SessionRunHistory sessionId={active.session.sessionId} open={open} /> }]} />
        <SessionArtifacts sessionId={active.session.sessionId} agentRunId={active.origin.initialAgentRunId} open={open} />
      </> : <>
        <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>Original task results stay here. Continue in chat to keep talking in the same Session.</Typography.Paragraph>
        <Flex vertical gap={token.paddingXS}>
          <Flex justify="flex-end">
            <Button icon={<CheckOutlined aria-hidden="true" />} loading={markingRead} disabled={busy || page.loading || !page.items.length}
              onClick={() => void markAllRead()}>Mark all as read</Button>
          </Flex>
          {readFeedback?.kind === "error" ? <Alert type="error" showIcon title={readFeedback.message}
            action={<Button disabled={busy} onClick={() => void markAllRead()}>Retry mark all as read</Button>} /> : null}
          {readFeedback?.kind === "success" ? <Typography.Text type="secondary" role="status">{readFeedback.message}</Typography.Text> : null}
        </Flex>
        {page.loading ? <Spin aria-label="Loading background Sessions" /> : page.items.length === 0 && !page.error ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No background Sessions yet" /> : null}
        <ul ref={list} className="background-work-list" aria-label="Background Sessions">
          {page.items.map(item => <li className="background-work-item" key={item.session.sessionId} data-background-session-id={item.session.sessionId}>
            <Flex vertical gap={token.paddingXS}>
              <Flex wrap align="center" justify="space-between" gap={token.paddingXS}>
                <Typography.Title level={5} className="background-work-item-title">{item.originalTitle ?? "Background task"}</Typography.Title>
                {item.initialRun ? <AgentRunStatus run={item.initialRun} /> : null}
              </Flex>
              <Flex wrap gap={token.paddingXS}>
                <Typography.Text type="secondary">{runOriginLabel(item.origin.kind)} · <time dateTime={item.initialRun?.updatedAt ?? item.session.createdAt}>{formatChatTime(item.initialRun?.updatedAt ?? item.session.createdAt) ?? "Unknown time"}</time></Typography.Text>
                <Typography.Text type="secondary">{fileCount(item)}{item.initialRun && item.initialRun.attemptCount > 1 ? ` · Attempt ${item.initialRun.attemptCount} of ${item.initialRun.maxAttempts}` : ""}</Typography.Text>
              </Flex>
              {item.initialRun?.outcome?.summary || item.initialRun?.failureSummary ? <Typography.Paragraph style={{ marginBottom: 0 }} ellipsis={{ rows: 2 }}>{item.initialRun.outcome?.summary || item.initialRun.failureSummary}</Typography.Paragraph> : null}
              {item.completionDelivery ? <CompletionDeliveryStatus delivery={item.completionDelivery} onInspect={id => void inspectHandlingRun(id)} /> : null}
              {item.surfaces.includes("ChatList") ? <Typography.Text type="secondary">Continued in chat</Typography.Text> : null}
              {item.initialRun?.progress ? <Typography.Text className="background-work-progress">{item.initialRun.progress}</Typography.Text> : null}
              {item.initialRun && item.completionDelivery?.status !== "handled" && isUnread(item.initialRun) ? <Typography.Text><BellOutlined /> Unread · needs attention</Typography.Text> : null}
              <Flex wrap align="center" gap={token.paddingXS}><Button data-background-control="history" onClick={() => select(item, "history")}>View original result</Button>
                <Button disabled={busy || !item.canContinueInChat} onClick={() => void openChat(item)}>{item.surfaces.includes("ChatList") ? "Open chat" : "Continue in chat"}</Button>{automationLink(item)}</Flex>
            </Flex>
          </li>)}
        </ul>
        {page.error ? <Alert type="error" showIcon title={page.error} /> : null}
        <DrawerListFooter loadingMore={page.loadingMore} hasMore={page.hasMore} error={page.error} count={page.items.length} onLoadMore={() => void page.loadMore()} onRetry={() => void page.retry()} />
      </>}
      {actionError ? <Alert type="error" showIcon title={actionError} action={<Button onClick={() => void page.refresh()}>Refresh</Button>} /> : null}
    </Flex>
  </Drawer><AgentRunDetailDrawer run={handlingRun} open={handlingOpen} wide={wide} onClose={() => setHandlingOpen(false)} onChange={setHandlingRun} /></>;
}

import { useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import {
  BellOutlined,
  CheckCircleOutlined,
  ClockCircleOutlined,
  CloseCircleOutlined,
  ExclamationCircleOutlined,
  InfoCircleOutlined,
  LoadingOutlined,
  RedoOutlined,
  StopOutlined
} from "@ant-design/icons";
import { Alert, App, Button, Drawer, Empty, Flex, Spin, Table, Tag, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import type { DrawerPageQuery, WorkItem, WorkItemResult } from "../../services/api";
import { formatChatTime } from "./chatTime";
import { DiagnosticDetails } from "./DiagnosticDetails";
import { useDrawerPages } from "./useDrawerPages";
import { DrawerListFooter } from "./DrawerListFooter";
import { useWorkReadState } from "./workReadState";

import { runOriginLabel, runStatusLabel, runOutcomeLabel, runTriggerLabel, runSource, type RunSource } from "./runPresentation";

const noRuns = async () => [];

/** One reading inset for long run instructions, approval previews and results. */
function RunTextSection({ label, item, children, result = false }: {
  label: string; item: WorkItem; children: string; result?: boolean;
}) {
  const { token } = theme.useToken();
  return <section className={`background-work-detail${result ? " background-work-result" : ""}`}>
    <Flex align="center" gap={token.paddingXS} className={result ? "background-work-detail-heading" : undefined}>
      {result ? <CheckCircleOutlined aria-hidden /> : null}
      <Typography.Text type="secondary" className="background-work-detail-label">{label}</Typography.Text>
    </Flex>
    <div role="region" aria-label={`${label} for ${item.automationName ?? runOriginLabel(item.origin)}`}
      tabIndex={0} className="background-work-long-content ac-scroll-pane">
      <Typography.Paragraph className="background-work-detail-body">{children}</Typography.Paragraph>
    </div>
  </section>;
}

function retryLabel(item: WorkItem, fallback: string) {
  if (item.status === "retrying" && item.attemptCount && item.maxAttempts) {
    return `Retrying · attempt ${item.attemptCount} of ${item.maxAttempts}`;
  }

  return fallback;
}

const statusPresentation: Record<string, { color?: string; icon: ReactNode }> = {
  queued: { icon: <ClockCircleOutlined /> },
  running: { color: "processing", icon: <LoadingOutlined /> },
  needsApproval: { color: "warning", icon: <ExclamationCircleOutlined /> },
  retrying: { color: "gold", icon: <RedoOutlined /> },
  completed: { color: "success", icon: <CheckCircleOutlined /> },
  failed: { color: "error", icon: <CloseCircleOutlined /> },
  cancelled: { icon: <StopOutlined /> }
};

export function BackgroundWorkDrawer({
  sessionId,
  open,
  wide,
  refreshKey = 0,
  pollIntervalMs = 5_000,
  onClose,
  load,
  loadResult,
  cancel,
  approve,
  reject,
  inline = false,
  selectedWorkItemId,
  loadOne,
  onSource,
  onRun,
  detailsOnly = false,
  afterClose
}: {
  onRun?: (workId: string) => void;
  detailsOnly?: boolean;
  afterClose?: () => void;
  inline?: boolean;
  selectedWorkItemId?: string;
  loadOne?: (owner: string, workId: string) => Promise<WorkItem>;
  onSource?: (source: RunSource) => void;
  sessionId: string;
  open: boolean;
  wide: boolean;
  refreshKey?: number;
  pollIntervalMs?: number;
  onClose: () => void;
  load: (sessionId: string, query?: DrawerPageQuery) => Promise<WorkItem[]>;
  loadResult: (sessionId: string, workItemId: string) => Promise<WorkItemResult>;
  cancel: (sessionId: string, workItemId: string, expectedRevision: number) => Promise<WorkItem>;
  approve: (
    sessionId: string,
    workItemId: string,
    approvalId: string,
    expectedRevision: number,
    expectedApprovalRevision: number,
    actionHash: string
  ) => Promise<WorkItem>;
  reject: (
    sessionId: string,
    workItemId: string,
    approvalId: string,
    expectedRevision: number,
    expectedApprovalRevision: number,
    actionHash: string
  ) => Promise<WorkItem>;
}) {
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const { items, updateItems, loading, loadingMore, hasMore, error, setError, loadMore, retry, captureScope } = useDrawerPages({
    scope: sessionId, open, refreshKey, pollIntervalMs, load: detailsOnly ? noRuns : load, id: item => item.workItemId
  });
  const { isUnread, markRead } = useWorkReadState();
  const [markingRead, setMarkingRead] = useState(false);
  const resultCache = useRef<Record<string, { revision: number; result: Promise<WorkItemResult> }>>({});
  const [results, setResults] = useState<Record<string, WorkItemResult>>({});
  const [resultErrors, setResultErrors] = useState<Record<string, string>>({});
  const [resultRetry, setResultRetry] = useState(0);
  const [busyId, setBusyId] = useState<string | null>(null);

  const [selected, setSelected] = useState<{ owner: string; item: WorkItem } | null>(null);
  const [selectionError, setSelectionError] = useState<string | null>(null);
  const selectionGeneration = useRef(0);
  const actionPending = useRef(false);
  const selectedItem = selected?.owner === sessionId && selected.item.workItemId === selectedWorkItemId ? selected.item : null;
  const pageItem = items.find(item => item.workItemId === selectedWorkItemId);
  const target = pageItem && (!selectedItem || pageItem.revision >= selectedItem.revision) ? pageItem : selectedItem;
  const displayedItems = target ? [target, ...items.filter(item => item.workItemId !== target.workItemId)] : items;

  useEffect(() => {
    setMarkingRead(false);
    setBusyId(null);
    actionPending.current = false;
  }, [sessionId, open]);

  useEffect(() => {
    let current = true;
    setSelectionError(null);
    setSelected(null);
    async function readSelected() {
      if (!open || !selectedWorkItemId || !loadOne || actionPending.current) return;
      const generation = ++selectionGeneration.current;
      try {
        const item = await loadOne(sessionId, selectedWorkItemId);
        if (current && generation === selectionGeneration.current) {
          setSelected(previous => previous?.owner === sessionId && previous.item.workItemId === item.workItemId && previous.item.revision > item.revision ? previous : { owner: sessionId, item });
          setSelectionError(null);
        }
      } catch (reason) {
        if (current && generation === selectionGeneration.current) setSelectionError(reason instanceof Error ? reason.message : "This run could not be loaded. Return to Automation and refresh its source.");
      }
    }
    void readSelected();
    const timer = open && selectedWorkItemId && pollIntervalMs ? window.setInterval(() => void readSelected(), pollIntervalMs) : undefined;
    return () => { current = false; selectionGeneration.current++; window.clearInterval(timer); };
  }, [open, sessionId, selectedWorkItemId, loadOne, pollIntervalMs]);

  const focusedSelection = useRef<string | undefined>(undefined);
  useEffect(() => {
    if (!open) { focusedSelection.current = undefined; return; }
    const key = `${sessionId}/${selectedWorkItemId}`;
    if (!target || loading || focusedSelection.current === key) return;
    const frame = requestAnimationFrame(() => {
      const row = document.querySelector<HTMLElement>(`[data-work-item-id="${selectedWorkItemId}"]`);
      row?.scrollIntoView({ block: "nearest" });
      row?.focus({ preventScroll: true });
      focusedSelection.current = key;
    });
    return () => cancelAnimationFrame(frame);
  }, [target, loading, open, selectedWorkItemId, sessionId]);

  useEffect(() => {
    if (!open || inline) {
      return;
    }

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || event.isComposing || event.defaultPrevented) {
        return;
      }

      const target = event.target;
      if (!(target instanceof Node)) {
        return;
      }

      const panel = document.querySelector(".background-work-drawer");
      if (panel?.contains(target)) {
        return;
      }

      if (
        target instanceof Element &&
        target.closest(".ant-modal, .ant-select-dropdown, .ant-dropdown, .ant-popover")
      ) {
        return;
      }

      if (document.querySelector(".ant-modal-wrap")) {
        return;
      }

      onClose();
    };

    window.addEventListener("keydown", onKeyDown, true);
    return () => window.removeEventListener("keydown", onKeyDown, true);
  }, [open, onClose, inline]);

  useEffect(() => {
    resultCache.current = {};
    setResults({});
    setResultErrors({});
  }, [sessionId]);

  useEffect(() => {
    if (!open || onRun) return;
    let current = true;
    async function readResults() {
      const completed = displayedItems.filter(item => item.status === "completed");
      const failures: Record<string, string> = {};
      const loaded = await Promise.all(completed.map(async item => {
        const cache = resultCache.current;
        let cached = cache[item.workItemId];
        if (cached?.revision !== item.revision) {
          const result = loadResult(sessionId, item.workItemId);
          cached = { revision: item.revision, result };
          cache[item.workItemId] = cached;
        }
        try {
          return [item.workItemId, await cached.result] as const;
        } catch (reason) {
          failures[item.workItemId] = reason instanceof Error ? reason.message : "The result could not be loaded.";
          if (cache[item.workItemId] === cached) delete cache[item.workItemId];
          return null;
        }
      }));
      if (current) { setResults(Object.fromEntries(loaded.filter(row => row !== null))); setResultErrors(failures); }
    }
    void readResults();
    return () => { current = false; };
  }, [open, sessionId, items, selected, selectedWorkItemId, loadResult, resultRetry, onRun]);

  async function markAllRead() {
    const isCurrent = captureScope();
    if (!isCurrent()) return;
    setMarkingRead(true);
    setError(null);
    try {
      let before: string | undefined;
      const attention: WorkItem[] = [];
      while (true) {
        const batch = await load(sessionId, { limit: 100, before, attentionOnly: true });
        if (!isCurrent()) return;
        attention.push(...batch);
        if (batch.length < 100) break;
        const next = batch[batch.length - 1].workItemId;
        if (next === before) throw new Error("Unable to finish loading unread work. Try again.");
        before = next;
      }
      markRead(attention);
    } catch (reason) {
      if (isCurrent()) setError(reason instanceof Error ? reason.message : "Unable to mark background work as read.");
    } finally { if (isCurrent()) setMarkingRead(false); }
  }

  async function retrySelection() {
    if (!loadOne || !selectedWorkItemId) return;
    const isCurrent = captureScope();
    const generation = ++selectionGeneration.current;
    try {
      const item = await loadOne(sessionId, selectedWorkItemId);
      if (isCurrent() && generation === selectionGeneration.current) {
        setSelected(previous => previous?.owner === sessionId && previous.item.workItemId === item.workItemId && previous.item.revision > item.revision ? previous : { owner: sessionId, item });
        setSelectionError(null);
      }
    } catch (reason) {
      if (isCurrent() && generation === selectionGeneration.current) setSelectionError(reason instanceof Error ? reason.message : "This run could not be loaded.");
    }
  }

  function replace(updated: WorkItem) {
    selectionGeneration.current++;
    setSelected(previous => previous?.owner === sessionId && previous.item.workItemId === updated.workItemId && previous.item.revision <= updated.revision ? { owner: sessionId, item: updated } : previous);
    updateItems((current) => current.map((row) => (row.workItemId === updated.workItemId && updated.revision >= row.revision ? updated : row)));
    if (updated.status !== "completed") {
      setResults((current) => {
        const next = { ...current };
        delete next[updated.workItemId];
        return next;
      });
    }
  }

  async function confirmCancel(item: WorkItem) {
    const isCurrent = captureScope();
    if (!isCurrent()) return;
    if (actionPending.current) return;
    actionPending.current = true; selectionGeneration.current++;
    setBusyId(item.workItemId);
    setError(null);
    try {
      const updated = await cancel(sessionId, item.workItemId, item.revision);
      if (isCurrent()) replace(updated);
    } catch (reason: unknown) {
      if (isCurrent()) setError(reason instanceof Error ? reason.message : "Unable to cancel the work.");
    } finally {
      if (isCurrent()) { setBusyId(null); actionPending.current = false; }
    }
  }

  async function confirmDecision(item: WorkItem, decision: "approve" | "reject") {
    const isCurrent = captureScope();
    if (!isCurrent()) return;
    if (!item.approvalId || item.approvalRevision == null || !item.actionHash) {
      return;
    }

    if (actionPending.current) return;
    actionPending.current = true; selectionGeneration.current++;
    setBusyId(item.workItemId);
    setError(null);
    try {
      const updated = decision === "approve"
        ? await approve(sessionId, item.workItemId, item.approvalId, item.revision, item.approvalRevision, item.actionHash)
        : await reject(sessionId, item.workItemId, item.approvalId, item.revision, item.approvalRevision, item.actionHash);
      if (isCurrent()) replace(updated);
    } catch (reason: unknown) {
      if (isCurrent()) setError(reason instanceof Error ? reason.message : "Unable to update the approval.");
    } finally {
      if (isCurrent()) { setBusyId(null); actionPending.current = false; }
    }
  }

  function renderItem(item: WorkItem) {
    const status = statusPresentation[item.status] ?? {
      icon: <InfoCircleOutlined />
    };
    const busy = busyId !== null;
    const source = onSource ? runSource(item) : null;

    return (
      <li key={item.workItemId} data-work-item-id={item.workItemId} tabIndex={item.workItemId === selectedWorkItemId ? -1 : undefined}
        className={`background-work-item${item.workItemId === selectedWorkItemId ? " background-work-selected" : ""}`}>
        <Flex vertical gap={token.paddingSM} className="background-work-item-content">
          <Flex align="flex-start" justify="space-between" gap={token.paddingSM} wrap>
            <Flex vertical className="background-work-item-title">
              <Typography.Text strong className="background-work-origin" aria-label={`Source: ${runOriginLabel(item.origin)}`}>
                {item.automationName ?? runOriginLabel(item.origin)}
              </Typography.Text>
            </Flex>
            <Tag
              variant="filled"
              color={status.color}
              icon={status.icon}
              className="background-work-status"
            >
              {retryLabel(item, runStatusLabel(item.status))}
            </Tag>
          </Flex>

          <Flex wrap gap={token.paddingXS} className="background-work-timestamps">
          <Typography.Text type="secondary">Created <time dateTime={item.createdAt}>{formatChatTime(item.createdAt) ?? item.createdAt}</time></Typography.Text>
          {results[item.workItemId]?.completedAt ? <Typography.Text type="secondary">Completed <time dateTime={results[item.workItemId].completedAt}>{formatChatTime(results[item.workItemId].completedAt) ?? results[item.workItemId].completedAt}</time></Typography.Text> : null}
          <Typography.Text type="secondary">Updated <time dateTime={item.updatedAt}>{formatChatTime(item.updatedAt) ?? item.updatedAt}</time></Typography.Text>
          </Flex>
          {item.triggerSummary ? <Typography.Text type="secondary" className="background-work-trigger">{runTriggerLabel(item.triggerSummary)}</Typography.Text> : null}
          <dl className="background-work-metadata">
            {item.modelKey ? <><dt>Model</dt><dd>{item.modelKey}</dd></> : null}
            {item.outcome ? <><dt>Outcome</dt><dd>{runOutcomeLabel(item.outcome)}</dd></> : null}
            <dt>Run</dt><dd>{item.workItemId}</dd>
          </dl>
          {item.instructions ? <RunTextSection label="Instructions" item={item}>{item.instructions}</RunTextSection> : null}
          {item.sourceId && item.origin === "Automation · Manual" && !item.automationId ? <Typography.Text type="secondary" style={{ overflowWrap: "anywhere" }}>Checkpoint: {item.sourceId}</Typography.Text> : null}
          {item.progress ? (
            <Typography.Text type="secondary" className="background-work-progress">
              {item.progress}
            </Typography.Text>
          ) : null}

          {item.knownEffect ? (
            <Flex align="flex-start" gap={token.paddingXS} className="background-work-effect">
              <InfoCircleOutlined aria-hidden />
              <Typography.Text type="secondary">{item.knownEffect}</Typography.Text>
            </Flex>
          ) : null}

          {item.status === "retrying" && item.failureSummary ? (
            <Typography.Text type="secondary">{item.failureSummary}</Typography.Text>
          ) : null}

          {item.status === "failed" ? (
            <Alert
              type="error"
              showIcon
              title="Work failed"
              description={item.failureSummary ?? "This work failed."}
              className="background-work-alert"
              action={item.diagnosticId ? (
                <DiagnosticDetails
                  fields={{
                    diagnosticId: item.diagnosticId,
                    sessionId,
                    workItemId: item.workItemId,
                    code: item.failureCode,
                    triggerRegistrationId: item.automationId,
                    triggerOccurrenceId: item.sourceId
                  }}
                />
              ) : undefined}
            />
          ) : null}

          {item.needsApproval && item.approvalPreview ? (
            <RunTextSection label="Approval required" item={item}>{item.approvalPreview}</RunTextSection>
          ) : null}

          {isUnread(item) ? (
            <Flex align="center" gap={token.paddingXS} className="background-work-attention">
              <BellOutlined aria-hidden />
              <Typography.Text>Needs attention</Typography.Text>
              <Button size="small" onClick={() => {
                try { markRead([item]); } catch (reason) { setError((reason as Error).message); }
              }}>Mark as read</Button>
            </Flex>
          ) : null}

          {item.status === "completed" && !results[item.workItemId] && !resultErrors[item.workItemId] ? <Spin aria-label="Loading run result" /> : null}
          {resultErrors[item.workItemId] ? <Alert type="error" showIcon title="Run result could not be loaded" description={resultErrors[item.workItemId]} action={<Button onClick={() => setResultRetry(value => value + 1)}>Retry result</Button>} /> : null}
          {results[item.workItemId] ? (
            <RunTextSection label="Result" item={item} result>{results[item.workItemId].text}</RunTextSection>
          ) : null}

          {source || item.needsApproval || item.cancellationAvailable ? (
            <Flex component="section" vertical gap={token.paddingXS} aria-label="Run actions">
              <Typography.Text strong>Actions</Typography.Text>
              <Flex gap={token.paddingSM} wrap="wrap" justify="space-between" align="center">
                {source ? <Button className="admin-run-source" onClick={() => onSource?.(source)}>View {source.kind === "experience" ? "experience" : "automation"}</Button> : null}
                {item.needsApproval || item.cancellationAvailable ? <Flex gap={token.paddingXS} wrap="wrap" justify="flex-end" className="background-work-actions" style={{ marginInlineStart: "auto" }}>
                  {item.needsApproval && item.approvalId && item.actionHash ? (
                    <>
                      <Button
                        type="primary"
                        disabled={busy}
                        aria-label={`Approve ${runOriginLabel(item.origin)}`}
                        onClick={() =>
                          confirmAction(modal, {
                            title: "Approve this action?",
                            content: "The action will continue immediately.",
                            okText: "Approve action",
                            cancelText: "Keep waiting",
                            onOk: () => confirmDecision(item, "approve")
                          })
                        }
                      >
                        Approve
                      </Button>
                      <Button
                        disabled={busy}
                        aria-label={`Reject ${runOriginLabel(item.origin)}`}
                        onClick={() =>
                          confirmAction(modal, {
                            title: "Reject this action?",
                            content: "The run will continue without this action.",
                            okText: "Reject action",
                            cancelText: "Keep waiting",
                            danger: true,
                            onOk: () => confirmDecision(item, "reject")
                          })
                        }
                      >
                        Reject
                      </Button>
                    </>
                  ) : null}
                  {item.cancellationAvailable ? (
                    <Button
                      danger
                      disabled={busy}
                      aria-label={`Cancel ${runOriginLabel(item.origin)}`}
                      onClick={() =>
                        confirmAction(modal, {
                          title: "Cancel this work?",
                          content: "Any external action that already completed cannot be undone.",
                          okText: "Cancel work",
                          cancelText: "Keep",
                          danger: true,
                          onOk: () => confirmCancel(item)
                        })
                      }
                    >
                      Cancel
                    </Button>
                  ) : null}
                </Flex> : null}
              </Flex>
            </Flex>
          ) : null}
        </Flex>
      </li>
    );
  }

  const content = (
      <Flex vertical gap={token.paddingSM}>
        {!detailsOnly ? <Flex justify="flex-end">
          <Button className="background-work-read-all" aria-label="Mark all as read" loading={markingRead} disabled={markingRead || loading || items.length === 0} onClick={() => void markAllRead()}>Mark all as read</Button>
        </Flex> : null}
        {selectionError ? <Alert type="error" showIcon title="The selected run could not be loaded" description={selectionError} action={<Button onClick={() => void retrySelection()}>Retry selected run</Button>} /> : null}
        {error ? <Alert type="error" showIcon title={error} /> : null}
        {selectedWorkItemId && loadOne && !target && !selectionError ? <Spin aria-label="Loading selected run" /> : null}
        {loading ? (
          <Flex justify="center" className="background-work-loading">
            <Spin aria-label="Loading background work" />
          </Flex>
        ) : (
          onRun ? <Table<WorkItem> aria-label="Runs table" className="admin-collection-table" size="small"
            rowKey="workItemId" dataSource={displayedItems} pagination={false} scroll={{ x: 1090 }}
            locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No runs yet. Runs appear when Automations or manual reviews execute." /> }}
            onRow={item => ({ "data-run-id": item.workItemId, tabIndex: -1, className: "admin-run-row",
              onClick: event => {
                if (event.target instanceof Element && event.target.closest("button")) return;
                event.currentTarget.focus(); onRun(item.workItemId);
              }
            })}
            columns={[
              { title: "Automation", key: "name", width: 180, render: (_, item) => item.automationName ?? runOriginLabel(item.origin) },
              { title: "Instructions", dataIndex: "instructions", key: "instructions", width: 380, ellipsis: true,
                render: (intent: string | null | undefined) => intent ? <span title={intent}>{intent}</span> : <Typography.Text type="secondary">—</Typography.Text> },
              { title: "Run ID", key: "id", width: 290, ellipsis: true, render: (_, item) =>
                <Button type="link" size="small" className="admin-collection-name" title={item.workItemId}
                  aria-label={`View ${runOriginLabel(item.origin).toLowerCase()} run ${item.workItemId}`}
                  onClick={() => onRun(item.workItemId)}>{item.workItemId}</Button> },
              { title: "Status", key: "status", width: 150, render: (_, item) => {
                const status = statusPresentation[item.status] ?? { icon: <InfoCircleOutlined /> };
                return <Flex vertical align="flex-start" gap={token.paddingXS}>
                  <Tag variant="filled" color={status.color} icon={status.icon} className="background-work-status" title={retryLabel(item, runStatusLabel(item.status))}>{runStatusLabel(item.status)}</Tag>
                  {isUnread(item) ? <Typography.Text><BellOutlined /> Needs attention</Typography.Text> : null}
                </Flex>;
              } },
              { title: "Updated", key: "updated", width: 140, render: (_, item) => <time dateTime={item.updatedAt}>{formatChatTime(item.updatedAt) ?? item.updatedAt}</time> }
            ]} /> : displayedItems.length ? <ul className="background-work-list">{(detailsOnly ? (target ? [target] : []) : displayedItems).map(renderItem)}</ul> :
            detailsOnly ? null : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No runs yet. Runs appear when Automations or manual reviews execute." className="background-work-empty" />
        )}
        {!loading && !detailsOnly ? <DrawerListFooter loadingMore={loadingMore} hasMore={hasMore} error={error} count={items.length}
          onLoadMore={() => void loadMore()} onRetry={retry} /> : null}
      </Flex>
  );
  if (inline) return <section aria-label="Runs" className="admin-definition-panel">
    <div className="admin-definition-panel-heading"><Typography.Title level={4}>Runs</Typography.Title>
      <Typography.Text type="secondary">Execution history from Automations and manual review.</Typography.Text></div>
    <div className="admin-definition-panel-body">{content}</div>
  </section>;
  return <Drawer title={<Flex vertical gap={0}><Typography.Text strong id="background-work-drawer-title">{detailsOnly ? "Run details" : "Background work"}</Typography.Text>{!detailsOnly ? <Typography.Text type="secondary" className="background-work-subtitle">Runs from Automations and manual review</Typography.Text> : null}</Flex>} aria-labelledby="background-work-drawer-title" placement="right" size={wide ? "min(640px, 100vw)" : "100vw"} afterOpenChange={visible => { if (!visible) afterClose?.(); }} open={open} onClose={onClose}
    onKeyDown={event => { if (event.key === "Escape") { event.stopPropagation(); onClose(); } }} className={`background-work-drawer${detailsOnly ? " run-details-drawer" : ""}`}>{content}</Drawer>;
}

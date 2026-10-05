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
import { Alert, App, Button, Drawer, Empty, Flex, List, Spin, Tag, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import type { DrawerPageQuery, WorkItem, WorkItemResult } from "../../services/api";
import { formatChatTime } from "./chatTime";
import { DiagnosticDetails } from "./DiagnosticDetails";
import { useDrawerPages } from "./useDrawerPages";
import { DrawerListFooter } from "./DrawerListFooter";
import { useWorkReadState } from "./workReadState";

function retryLabel(item: WorkItem, fallback: string) {
  if (item.status === "retrying" && item.attemptCount && item.maxAttempts) {
    return `Retrying · attempt ${item.attemptCount} of ${item.maxAttempts}`;
  }

  return fallback;
}

const statusPresentation: Record<string, { label: string; color?: string; icon: ReactNode }> = {
  queued: { label: "Queued", icon: <ClockCircleOutlined /> },
  running: { label: "Running", color: "processing", icon: <LoadingOutlined /> },
  needsApproval: { label: "Needs approval", color: "warning", icon: <ExclamationCircleOutlined /> },
  retrying: { label: "Retrying", color: "gold", icon: <RedoOutlined /> },
  completed: { label: "Completed", color: "success", icon: <CheckCircleOutlined /> },
  failed: { label: "Failed", color: "error", icon: <CloseCircleOutlined /> },
  cancelled: { label: "Cancelled", icon: <StopOutlined /> }
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
  reject
}: {
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
    scope: sessionId, open, refreshKey, pollIntervalMs, load, id: item => item.workItemId
  });
  const { isUnread, markRead } = useWorkReadState();
  const [markingRead, setMarkingRead] = useState(false);
  const resultCache = useRef<Record<string, { revision: number; result: Promise<string> }>>({});
  const [results, setResults] = useState<Record<string, string>>({});
  const [busyId, setBusyId] = useState<string | null>(null);

  useEffect(() => {
    if (!open) {
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
  }, [open, onClose]);

  useEffect(() => {
    resultCache.current = {};
    setResults({});
  }, [sessionId]);

  useEffect(() => {
    if (!open) return;
    let current = true;
    async function readResults() {
      const completed = items.filter(item => item.status === "completed");
      const loaded = await Promise.all(completed.map(async item => {
        const cache = resultCache.current;
        let cached = cache[item.workItemId];
        if (cached?.revision !== item.revision) {
          const result = loadResult(sessionId, item.workItemId).then(response => response.text);
          cached = { revision: item.revision, result };
          cache[item.workItemId] = cached;
        }
        try {
          return [item.workItemId, await cached.result] as const;
        } catch {
          if (cache[item.workItemId] === cached) delete cache[item.workItemId];
          return [item.workItemId, ""] as const;
        }
      }));
      if (current) setResults(Object.fromEntries(loaded));
    }
    void readResults();
    return () => { current = false; };
  }, [open, sessionId, items, loadResult]);

  useEffect(() => {
    setMarkingRead(false);
    setBusyId(null);
  }, [sessionId, open]);

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

  function replace(updated: WorkItem) {
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
    setBusyId(item.workItemId);
    setError(null);
    try {
      const updated = await cancel(sessionId, item.workItemId, item.revision);
      if (isCurrent()) replace(updated);
    } catch (reason: unknown) {
      if (isCurrent()) setError(reason instanceof Error ? reason.message : "Unable to cancel the work.");
    } finally {
      if (isCurrent()) setBusyId(null);
    }
  }

  async function confirmDecision(item: WorkItem, decision: "approve" | "reject") {
    const isCurrent = captureScope();
    if (!isCurrent()) return;
    if (!item.approvalId || item.approvalRevision == null || !item.actionHash) {
      return;
    }

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
      if (isCurrent()) setBusyId(null);
    }
  }

  function renderItem(item: WorkItem) {
    const status = statusPresentation[item.status] ?? {
      label: item.status,
      icon: <InfoCircleOutlined />
    };
    const busy = busyId === item.workItemId;

    return (
      <List.Item className="background-work-item">
        <Flex vertical gap={token.paddingSM} className="background-work-item-content">
          <Flex align="flex-start" justify="space-between" gap={token.paddingSM}>
            <Flex vertical>
              <Typography.Text strong className="background-work-origin" aria-label={`Source: ${item.origin}`}>
                {item.origin}
              </Typography.Text>
              <Typography.Text type="secondary">
                <time dateTime={item.updatedAt}>{formatChatTime(item.updatedAt) ?? item.updatedAt}</time>
              </Typography.Text>
            </Flex>
            <Tag
              variant="filled"
              color={status.color}
              icon={status.icon}
              className="background-work-status"
            >
              {retryLabel(item, status.label)}
            </Tag>
          </Flex>

          {item.modelKey ? <Typography.Text type="secondary">Model: {item.modelKey}{item.thoughtOutcome ? ` · ${item.thoughtOutcome}` : ""}</Typography.Text> : null}
          {item.registrationId ? <Typography.Text type="secondary" style={{ overflowWrap: "anywhere" }}>Registration: {item.registrationId}</Typography.Text> : null}
          {item.sourceId && item.origin === "Retrospection" ? <Typography.Text type="secondary" style={{ overflowWrap: "anywhere" }}>Checkpoint: {item.sourceId}</Typography.Text> : null}
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
                    triggerRegistrationId: item.triggerRegistrationId,
                    triggerOccurrenceId: item.sourceOccurrenceId
                  }}
                />
              ) : undefined}
            />
          ) : null}

          {item.needsApproval && item.approvalPreview ? (
            <div className="background-work-detail">
              <Typography.Text type="secondary" className="background-work-detail-label">
                Approval required
              </Typography.Text>
              <Typography.Paragraph className="background-work-detail-body">
                {item.approvalPreview}
              </Typography.Paragraph>
            </div>
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

          {results[item.workItemId] ? (
            <div className="background-work-detail background-work-result">
              <Flex align="center" gap={token.paddingXS} className="background-work-detail-heading">
                <CheckCircleOutlined aria-hidden />
                <Typography.Text type="secondary" className="background-work-detail-label">
                  Result
                </Typography.Text>
              </Flex>
              <Typography.Paragraph className="background-work-detail-body">
                {results[item.workItemId]}
              </Typography.Paragraph>
            </div>
          ) : null}

          {item.needsApproval || item.cancellationAvailable ? (
            <Flex gap={token.paddingXS} wrap="wrap" justify="flex-end" className="background-work-actions">
              {item.needsApproval && item.approvalId && item.actionHash ? (
                <>
                  <Button
                    type="primary"
                    disabled={busy}
                    aria-label={`Approve ${item.origin}`}
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
                    aria-label={`Reject ${item.origin}`}
                    onClick={() =>
                      confirmAction(modal, {
                        title: "Reject this action?",
                        content: "The background work will continue without this action.",
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
                  aria-label={`Cancel ${item.origin}`}
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
            </Flex>
          ) : null}
        </Flex>
      </List.Item>
    );
  }

  return (
    <Drawer
      title={
        <Flex vertical gap={0}>
          <Typography.Text strong id="background-work-drawer-title">
            Background work
          </Typography.Text>
          <Typography.Text type="secondary" className="background-work-subtitle">
            Tasks outside this conversation
          </Typography.Text>
        </Flex>
      }
      aria-labelledby="background-work-drawer-title"
      placement="right"
      size={wide ? 400 : 320}
      open={open}
      onClose={onClose}
      onKeyDown={(event) => {
        if (event.key !== "Escape") {
          return;
        }
        event.stopPropagation();
        onClose();
      }}
      className="background-work-drawer"
    >
      <Flex vertical gap={token.paddingSM}>
        <Flex justify="flex-end">
          <Button className="background-work-read-all" aria-label="Mark all as read" loading={markingRead} disabled={markingRead || loading || items.length === 0} onClick={() => void markAllRead()}>Mark all as read</Button>
        </Flex>
        {error ? <Alert type="error" showIcon title={error} /> : null}
        {loading ? (
          <Flex justify="center" className="background-work-loading">
            <Spin aria-label="Loading background work" />
          </Flex>
        ) : (
          <List
            dataSource={items}
            locale={{
              emptyText: (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="No background work yet"
                  className="background-work-empty"
                />
              )
            }}
            renderItem={renderItem}
            className="background-work-list"
          />
        )}
        {!loading ? <DrawerListFooter loadingMore={loadingMore} hasMore={hasMore} error={error} count={items.length}
          onLoadMore={() => void loadMore()} onRetry={retry} /> : null}
      </Flex>
    </Drawer>
  );
}

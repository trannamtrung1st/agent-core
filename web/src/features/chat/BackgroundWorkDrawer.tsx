import { useEffect, useState } from "react";
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
import type { WorkItem, WorkItemResult } from "../../services/api";
import { DiagnosticDetails } from "./DiagnosticDetails";

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
  load: (sessionId: string) => Promise<WorkItem[]>;
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
  const [items, setItems] = useState<WorkItem[]>([]);
  const [results, setResults] = useState<Record<string, string>>({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  useEffect(() => {
    if (!open) {
      return;
    }

    let generation = 0;
    let timer: number | undefined;

    async function refresh(initial: boolean) {
      const request = ++generation;
      if (initial) {
        setLoading(true);
        setError(null);
      }

      try {
        const next = await load(sessionId);
        if (request !== generation) {
          return;
        }

        const completed = next.filter((item) => item.status === "completed");
        const loaded = await Promise.all(
          completed.map(async (item) => {
            try {
              const result = await loadResult(sessionId, item.workItemId);
              return [item.workItemId, result.text] as const;
            } catch {
              return [item.workItemId, ""] as const;
            }
          })
        );
        if (request !== generation) {
          return;
        }

        setItems(next);
        setResults(Object.fromEntries(loaded.filter(([, text]) => text.length > 0)));
        setError(null);
      } catch (reason: unknown) {
        if (request !== generation) {
          return;
        }

        if (initial) {
          setItems([]);
          setResults({});
        }
        setError(reason instanceof Error ? reason.message : "Unable to load background work.");
      } finally {
        if (request === generation) {
          setLoading(false);
        }
      }
    }

    void refresh(true);
    timer = window.setInterval(() => {
      void refresh(false);
    }, pollIntervalMs);
    return () => {
      generation += 1;
      window.clearInterval(timer);
    };
  }, [open, sessionId, refreshKey, pollIntervalMs, load, loadResult]);

  function replace(updated: WorkItem) {
    setItems((current) => current.map((row) => (row.workItemId === updated.workItemId ? updated : row)));
    if (updated.status !== "completed") {
      setResults((current) => {
        const next = { ...current };
        delete next[updated.workItemId];
        return next;
      });
    }
  }

  async function confirmCancel(item: WorkItem) {
    setBusyId(item.workItemId);
    setError(null);
    try {
      replace(await cancel(sessionId, item.workItemId, item.revision));
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : "Unable to cancel the work.");
    } finally {
      setBusyId(null);
    }
  }

  async function confirmDecision(item: WorkItem, decision: "approve" | "reject") {
    if (!item.approvalId || item.approvalRevision == null || !item.actionHash) {
      return;
    }

    setBusyId(item.workItemId);
    setError(null);
    try {
      const updated = decision === "approve"
        ? await approve(sessionId, item.workItemId, item.approvalId, item.revision, item.approvalRevision, item.actionHash)
        : await reject(sessionId, item.workItemId, item.approvalId, item.revision, item.approvalRevision, item.actionHash);
      replace(updated);
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : "Unable to update the approval.");
    } finally {
      setBusyId(null);
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
            <Typography.Text strong className="background-work-origin">
              {item.origin}
            </Typography.Text>
            <Tag
              variant="filled"
              color={status.color}
              icon={status.icon}
              className="background-work-status"
            >
              {status.label}
            </Tag>
          </Flex>

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

          {item.attentionRequired ? (
            <Flex align="center" gap={token.paddingXS} className="background-work-attention">
              <BellOutlined aria-hidden />
              <Typography.Text>Needs attention</Typography.Text>
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
      className="background-work-drawer"
    >
      <Flex vertical gap={token.paddingSM}>
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
      </Flex>
    </Drawer>
  );
}

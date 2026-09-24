import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import {
  CheckCircleOutlined,
  ClockCircleOutlined,
  CloseCircleOutlined,
  ExclamationCircleOutlined,
  InfoCircleOutlined,
  LoadingOutlined,
  RedoOutlined,
  StopOutlined
} from "@ant-design/icons";
import { Alert, Button, Drawer, Empty, Flex, List, Popconfirm, Spin, Tag, Typography, theme } from "antd";
import type { WorkItem, WorkItemResult } from "../../services/api";

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
                  <Popconfirm
                    title="Approve this action?"
                    description="The action will continue immediately."
                    okText="Approve action"
                    cancelText="Keep waiting"
                    okButtonProps={{ loading: busy }}
                    onConfirm={() => confirmDecision(item, "approve")}
                  >
                    <Button type="primary" disabled={busy} aria-label={`Approve ${item.origin}`}>
                      Approve
                    </Button>
                  </Popconfirm>
                  <Popconfirm
                    title="Reject this action?"
                    description="The background work will continue without this action."
                    okText="Reject action"
                    cancelText="Keep waiting"
                    okButtonProps={{ danger: true, loading: busy }}
                    onConfirm={() => confirmDecision(item, "reject")}
                  >
                    <Button disabled={busy} aria-label={`Reject ${item.origin}`}>
                      Reject
                    </Button>
                  </Popconfirm>
                </>
              ) : null}
              {item.cancellationAvailable ? (
                <Popconfirm
                  title="Cancel this work?"
                  description="Any external action that already completed cannot be undone."
                  okText="Cancel work"
                  cancelText="Keep"
                  okButtonProps={{ danger: true, loading: busy }}
                  onConfirm={() => confirmCancel(item)}
                >
                  <Button danger disabled={busy} aria-label={`Cancel ${item.origin}`}>
                    Cancel
                  </Button>
                </Popconfirm>
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

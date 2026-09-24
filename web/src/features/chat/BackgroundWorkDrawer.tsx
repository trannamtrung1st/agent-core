import { useEffect, useState } from "react";
import { Alert, Button, Drawer, Flex, List, Popconfirm, Spin, Tag, Typography, theme } from "antd";
import type { WorkItem, WorkItemResult } from "../../services/api";

const statusLabel: Record<string, string> = {
  queued: "Queued",
  running: "Running",
  needsApproval: "Needs approval",
  retrying: "Retrying",
  completed: "Completed",
  failed: "Failed",
  cancelled: "Cancelled"
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

  return (
    <Drawer
      title={<span id="background-work-drawer-title">Background work</span>}
      aria-labelledby="background-work-drawer-title"
      placement="right"
      size={wide ? 400 : 320}
      open={open}
      onClose={onClose}
    >
      <Flex vertical gap={token.paddingSM}>
        {error ? <Alert type="error" showIcon title={error} /> : null}
        {loading ? (
          <Flex justify="center">
            <Spin aria-label="Loading background work" />
          </Flex>
        ) : (
          <List
            dataSource={items}
            locale={{ emptyText: "No background work" }}
            renderItem={(item) => (
              <List.Item>
                <Flex vertical gap={token.paddingXS} style={{ minWidth: 0, width: "100%" }}>
                  <Typography.Text style={{ overflowWrap: "anywhere" }}>{item.origin}</Typography.Text>
                  <Tag>{statusLabel[item.status] ?? item.status}</Tag>
                  {item.progress ? <Typography.Text type="secondary">{item.progress}</Typography.Text> : null}
                  {item.knownEffect ? <Typography.Text type="secondary">{item.knownEffect}</Typography.Text> : null}
                  {item.status === "failed" ? (
                    <Typography.Text type="danger">{item.failureSummary ?? "This work failed."}</Typography.Text>
                  ) : null}
                  {item.needsApproval && item.approvalPreview ? (
                    <Typography.Text style={{ overflowWrap: "anywhere" }}>{item.approvalPreview}</Typography.Text>
                  ) : null}
                  {results[item.workItemId] ? (
                    <Flex vertical gap={token.paddingXS}>
                      <Typography.Text type="secondary">Result</Typography.Text>
                      <Typography.Text style={{ overflowWrap: "anywhere" }}>{results[item.workItemId]}</Typography.Text>
                    </Flex>
                  ) : null}
                  <Flex gap={token.paddingXS} wrap="wrap">
                    {item.needsApproval && item.approvalId && item.actionHash ? (
                      <>
                        <Popconfirm
                          title="Approve this action?"
                          okText="Approve action"
                          cancelText="Keep waiting"
                          okButtonProps={{ loading: busyId === item.workItemId }}
                          onConfirm={() => confirmDecision(item, "approve")}
                        >
                          <Button type="primary" size="small" aria-label={`Approve ${item.origin}`}>
                            Approve
                          </Button>
                        </Popconfirm>
                        <Popconfirm
                          title="Reject this action?"
                          okText="Reject action"
                          cancelText="Keep waiting"
                          okButtonProps={{ danger: true, loading: busyId === item.workItemId }}
                          onConfirm={() => confirmDecision(item, "reject")}
                        >
                          <Button size="small" aria-label={`Reject ${item.origin}`}>
                            Reject
                          </Button>
                        </Popconfirm>
                      </>
                    ) : null}
                    {item.cancellationAvailable ? (
                      <Popconfirm
                        title="Cancel this work?"
                        okText="Cancel work"
                        cancelText="Keep"
                        okButtonProps={{ danger: true, loading: busyId === item.workItemId }}
                        onConfirm={() => confirmCancel(item)}
                      >
                        <Button size="small" danger aria-label={`Cancel ${item.origin}`}>
                          Cancel
                        </Button>
                      </Popconfirm>
                    ) : null}
                  </Flex>
                </Flex>
              </List.Item>
            )}
          />
        )}
      </Flex>
    </Drawer>
  );
}

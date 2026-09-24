import { useEffect, useState } from "react";
import { Alert, Button, Drawer, Flex, List, Popconfirm, Spin, Tag, Typography, theme } from "antd";
import type { SessionTrigger } from "../../services/api";

const statusLabel: Record<string, string> = {
  active: "Active",
  completed: "Completed",
  cancelled: "Cancelled",
  expired: "Expired",
  suspendedPolicy: "Suspended"
};

export function ScheduleDrawer({
  sessionId,
  open,
  wide,
  refreshKey,
  onClose,
  load,
  cancel
}: {
  sessionId: string;
  open: boolean;
  wide: boolean;
  refreshKey: number;
  onClose: () => void;
  load: (sessionId: string) => Promise<SessionTrigger[]>;
  cancel: (sessionId: string, registrationId: string, expectedRevision: number) => Promise<SessionTrigger>;
}) {
  const { token } = theme.useToken();
  const [items, setItems] = useState<SessionTrigger[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [cancellingId, setCancellingId] = useState<string | null>(null);

  useEffect(() => {
    if (!open) {
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);
    load(sessionId)
      .then((next) => {
        if (!cancelled) {
          setItems(next);
        }
      })
      .catch((reason: unknown) => {
        if (!cancelled) {
          setItems([]);
          setError(reason instanceof Error ? reason.message : "Unable to load schedules.");
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [open, sessionId, refreshKey, load]);

  async function confirmCancel(item: SessionTrigger) {
    setCancellingId(item.registrationId);
    setError(null);
    try {
      const updated = await cancel(sessionId, item.registrationId, item.revision);
      setItems((current) => current.map((row) => (row.registrationId === updated.registrationId ? updated : row)));
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : "Unable to cancel the schedule.");
    } finally {
      setCancellingId(null);
    }
  }

  return (
    <Drawer
      title={<span id="schedule-drawer-title">Schedules</span>}
      aria-labelledby="schedule-drawer-title"
      placement="right"
      size={wide ? 400 : 320}
      open={open}
      onClose={onClose}
    >
      <Flex vertical gap={token.paddingSM}>
        {error ? <Alert type="error" showIcon title={error} /> : null}
        {loading ? (
          <Flex justify="center">
            <Spin aria-label="Loading schedules" />
          </Flex>
        ) : (
          <List
            dataSource={items}
            locale={{ emptyText: "No schedules" }}
            renderItem={(item) => (
              <List.Item
                actions={
                  item.status === "active"
                    ? [
                        <Popconfirm
                          key="cancel"
                          title="Cancel this schedule?"
                          okText="Cancel schedule"
                          cancelText="Keep"
                          okButtonProps={{ danger: true, loading: cancellingId === item.registrationId }}
                          onConfirm={() => confirmCancel(item)}
                        >
                          <Button type="link" danger aria-label={`Cancel ${item.intent}`}>
                            Cancel
                          </Button>
                        </Popconfirm>
                      ]
                    : undefined
                }
              >
                <Flex vertical gap={token.paddingXS} style={{ minWidth: 0, width: "100%" }}>
                  <Typography.Text style={{ overflowWrap: "anywhere" }}>{item.intent}</Typography.Text>
                  <Typography.Text type="secondary">{item.schedule}</Typography.Text>
                  <Flex gap={token.paddingXS} wrap="wrap">
                    <Tag>{item.timeZone}</Tag>
                    <Tag>{statusLabel[item.status] ?? item.status}</Tag>
                  </Flex>
                  {item.status === "suspendedPolicy" && item.suspensionReason ? (
                    <Typography.Text type="secondary">{item.suspensionReason}</Typography.Text>
                  ) : null}
                  {item.nextOccurrenceAt ? (
                    <Typography.Text type="secondary">
                      Next <time dateTime={item.nextOccurrenceAt}>{item.nextOccurrenceAt}</time>
                    </Typography.Text>
                  ) : null}
                </Flex>
              </List.Item>
            )}
          />
        )}
      </Flex>
    </Drawer>
  );
}

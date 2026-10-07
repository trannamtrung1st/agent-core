import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import {
  CalendarOutlined,
  CheckCircleOutlined,
  ClockCircleOutlined,
  GlobalOutlined,
  HourglassOutlined,
  PauseCircleOutlined,
  StopOutlined
} from "@ant-design/icons";
import { Alert, App, Button, Drawer, Empty, Flex, List, Spin, Tag, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import type { DrawerPageQuery, SessionAutomation } from "../../services/api";

import { useDrawerPages } from "./useDrawerPages";
import { DrawerListFooter } from "./DrawerListFooter";

const statusPresentation: Record<string, { label: string; color?: string; icon: ReactNode }> = {
  active: { label: "Active", color: "processing", icon: <ClockCircleOutlined /> },
  disabled: { label: "Disabled", icon: <PauseCircleOutlined /> },
  completed: { label: "Completed", color: "success", icon: <CheckCircleOutlined /> },
  cancelled: { label: "Cancelled", icon: <StopOutlined /> },
  expired: { label: "Expired", color: "gold", icon: <HourglassOutlined /> },
  suspendedPolicy: { label: "Suspended", color: "warning", icon: <PauseCircleOutlined /> }
};

function formatOccurrence(value: string, timeZone: string) {
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  try {
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: "medium",
      timeStyle: "short",
      timeZone
    }).format(parsed);
  } catch {
    return parsed.toLocaleString();
  }
}

export function AutomationDrawer({
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
  load: (sessionId: string, query?: DrawerPageQuery) => Promise<SessionAutomation[]>;
  cancel: (sessionId: string, automationId: string, expectedRevision: number) => Promise<SessionAutomation>;
}) {
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const { items, updateItems, loading, loadingMore, hasMore, error, setError, loadMore, retry, captureScope } = useDrawerPages({
    scope: sessionId, open, refreshKey, load, id: item => item.automationId
  });
  const [cancellingId, setCancellingId] = useState<string | null>(null);

  useEffect(() => { setCancellingId(null); }, [sessionId, open]);

  async function confirmCancel(item: SessionAutomation) {
    const isCurrent = captureScope();
    if (!isCurrent()) return;
    setCancellingId(item.automationId);
    setError(null);
    try {
      const updated = await cancel(sessionId, item.automationId, item.revision);
      if (isCurrent()) updateItems((current) => current.map((row) => (row.automationId === updated.automationId && updated.revision >= row.revision ? updated : row)));
    } catch (reason: unknown) {
      if (isCurrent()) setError(reason instanceof Error ? reason.message : "Unable to cancel the automation.");
    } finally {
      if (isCurrent()) setCancellingId(null);
    }
  }

  function renderItem(item: SessionAutomation) {
    const status = statusPresentation[item.status] ?? {
      label: item.status,
      icon: <CalendarOutlined />
    };
    const busy = cancellingId === item.automationId;

    return (
      <List.Item className="schedule-item">
        <Flex vertical gap={token.paddingSM} className="schedule-item-content">
          <Flex align="flex-start" justify="space-between" gap={token.paddingSM}>
            <Typography.Text strong className="schedule-intent">
              {item.instructions}
            </Typography.Text>
            <Tag variant="filled" color={status.color} icon={status.icon} className="schedule-status">
              {status.label}
            </Tag>
          </Flex>

          <div className="schedule-detail">
            <Flex align="center" gap={token.paddingXS} className="schedule-detail-heading">
              <CalendarOutlined aria-hidden />
              <Typography.Text type="secondary" className="schedule-detail-label">
                When
              </Typography.Text>
            </Flex>
            <Typography.Paragraph className="schedule-detail-body">{item.when}</Typography.Paragraph>
          </div>

          <Flex gap={token.paddingXS} align="center" wrap="wrap" className="schedule-meta">
            {item.timeZone ? <Tag icon={<GlobalOutlined />} className="schedule-timezone">
              {item.timeZone}
            </Tag> : null}
            {item.nextOccurrenceAt ? (
              <Typography.Text type="secondary" className="schedule-next">
                Next run{" "}
                <time dateTime={item.nextOccurrenceAt}>
                  {formatOccurrence(item.nextOccurrenceAt, item.timeZone ?? "UTC")}
                </time>
              </Typography.Text>
            ) : null}
          </Flex>

          {item.status === "suspendedPolicy" && item.suspensionReason ? (
            <Alert
              type="warning"
              showIcon
              title="Automation suspended"
              description={item.suspensionReason}
              className="schedule-alert"
            />
          ) : null}

          {item.status === "active" ? (
            <Flex gap={token.paddingXS} wrap="wrap" justify="flex-end" className="schedule-actions">
              <Button
                danger
                disabled={busy}
                aria-label={`Cancel ${item.instructions}`}
                onClick={() =>
                  confirmAction(modal, {
                    title: "Cancel this automation?",
                    content: "Future occurrences will not run.",
                    okText: "Delete automation",
                    cancelText: "Keep",
                    danger: true,
                    onOk: () => confirmCancel(item)
                  })
                }
              >
                Delete automation
              </Button>
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
          <Typography.Text strong id="schedule-drawer-title">
            Automations
          </Typography.Text>
          <Typography.Text type="secondary" className="schedule-subtitle">
            Automations owned by this agent
          </Typography.Text>
        </Flex>
      }
      aria-labelledby="schedule-drawer-title"
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
      className="schedule-drawer"
    >
      <Flex vertical gap={token.paddingSM}>
        {error ? <Alert type="error" showIcon title={error} /> : null}
        {loading ? (
          <Flex justify="center" className="schedule-loading">
            <Spin aria-label="Loading automations" />
          </Flex>
        ) : (
          <List
            dataSource={items}
            locale={{
              emptyText: (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="No automations yet"
                  className="schedule-empty"
                />
              )
            }}
            renderItem={renderItem}
            className="schedule-list"
          />
        )}
        {!loading ? <DrawerListFooter loadingMore={loadingMore} hasMore={hasMore} error={error} count={items.length}
          onLoadMore={() => void loadMore()} onRetry={retry} /> : null}
      </Flex>
    </Drawer>
  );
}

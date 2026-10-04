import type { ReactNode } from "react";
import { App as AntApp, Badge, Button, Flex, Tooltip, Typography } from "antd";
import { CalendarOutlined, InboxOutlined, PoweroffOutlined } from "@ant-design/icons";
import { confirmAction } from "../../app/confirmAction";
import { formatChatTime } from "./chatTime";

export function ChatHeader({
  title,
  subtitle,
  timestamp,
  sessionsToggle,
  speechLocale,
  onSchedules,
  onBackgroundWork,
  attentionCount = 0,
  onEnd,
  inSession
}: {
  title: string;
  subtitle?: string | null;
  timestamp?: string | null;
  sessionsToggle?: ReactNode;
  speechLocale?: ReactNode;
  onSchedules?: () => void;
  onBackgroundWork?: () => void;
  attentionCount?: number;
  onEnd: () => void;
  inSession: boolean;
}) {
  const { modal } = AntApp.useApp();
  const timeLabel = timestamp ? formatChatTime(timestamp) : null;

  return (
    <Flex align="center" justify="space-between" gap={12} wrap="wrap" className="chat-header-inner">
      <Flex align="center" gap={12} className="chat-header-identity">
        {sessionsToggle}
        <div className="chat-header-copy">
          <Flex align="baseline" gap={8} className="chat-header-title-row">
            <Typography.Title level={1} className="chat-header-title">
              {title}
            </Typography.Title>
            {timeLabel && timestamp ? (
              <Typography.Text type="secondary" className="chat-header-time">
                <time dateTime={timestamp}>{timeLabel}</time>
              </Typography.Text>
            ) : null}
          </Flex>
          {subtitle ? (
            <Typography.Text type="secondary" className="chat-header-subtitle">
              {subtitle}
            </Typography.Text>
          ) : null}
        </div>
      </Flex>
      {speechLocale || inSession || onBackgroundWork ? (
        <Flex align="center" gap={8} className="chat-header-actions">
          {speechLocale}
          {onBackgroundWork ? (
            <Badge count={attentionCount} size="small" offset={[-4, 4]}>
              <Tooltip title="Background work">
                <Button
                  type="text"
                  size="small"
                  className="chat-header-icon-action chat-header-background-work"
                  aria-label={attentionCount > 0 ? `Background work, ${attentionCount} need attention` : "Background work"}
                  icon={<InboxOutlined />}
                  onClick={onBackgroundWork}
                />
              </Tooltip>
            </Badge>
          ) : null}
          {inSession && onSchedules ? (
            <Tooltip title="Schedules">
              <Button
                type="text"
                size="small"
                className="chat-header-icon-action"
                aria-label="Schedules"
                icon={<CalendarOutlined />}
                onClick={onSchedules}
              />
            </Tooltip>
          ) : null}
          {inSession ? (
            <Tooltip title="End conversation">
              <Button
                type="text"
                size="small"
                className="chat-header-icon-action"
                aria-label="End"
                icon={<PoweroffOutlined />}
                onClick={() => {
                  confirmAction(modal, {
                    title: "End this conversation?",
                    content: "This conversation will become read-only and cannot be resumed.",
                    okText: "End",
                    danger: true,
                    onOk: onEnd
                  });
                }}
              />
            </Tooltip>
          ) : null}
        </Flex>
      ) : null}
    </Flex>
  );
}

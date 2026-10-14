import type { ReactNode, Ref } from "react";
import { App as AntApp, Badge, Button, Flex, Tooltip, Typography } from "antd";
import { CalendarOutlined, CodeOutlined, InboxOutlined, PoweroffOutlined } from "@ant-design/icons";
import { confirmAction } from "../../app/confirmAction";
import { formatChatTime } from "./chatTime";

export function ChatHeader({
  title,
  subtitle,
  timestamp,
  status,
  additionalActions,
  sessionsToggle,
  speechLocale,
  onSchedules,
  onBackgroundWork,
  onSandbox,
  sandboxButtonRef,
  attentionCount = 0,
  onEnd,
  inSession
}: {
  title: string;
  subtitle?: string | null;
  timestamp?: string | null;
  status?: ReactNode;
  additionalActions?: ReactNode;
  sessionsToggle?: ReactNode;
  speechLocale?: ReactNode;
  onSchedules?: () => void;
  onBackgroundWork?: () => void;
  onSandbox?: () => void;
  sandboxButtonRef?: Ref<HTMLButtonElement>;
  attentionCount?: number;
  onEnd: () => void;
  inSession: boolean;
}) {
  const { modal } = AntApp.useApp();
  const timeLabel = timestamp ? formatChatTime(timestamp) : null;

  return (
    <Flex align="center" justify="space-between" gap={12} className="chat-header-inner">
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
          {subtitle || status ? (
            <Flex align="center" gap={8} className="chat-header-details">
              {subtitle ? (
                <Tooltip title={subtitle}><Typography.Text type="secondary" className="chat-header-subtitle single-line-ellipsis">
                  {subtitle}
                </Typography.Text></Tooltip>
              ) : null}
              {status}
            </Flex>
          ) : null}
        </div>
      </Flex>
      {speechLocale || inSession || onBackgroundWork || additionalActions ? (
        <Flex align="center" wrap gap={8} className="chat-header-actions">
          {speechLocale}
          <Flex align="center" gap={8} className="chat-header-session-actions">
            {onSandbox && <Tooltip title="Session sandbox"><Button type="text" size="small" className="chat-header-icon-action" aria-label="Session sandbox" ref={sandboxButtonRef} icon={<CodeOutlined />} onClick={onSandbox} /></Tooltip>}
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
              <Tooltip title="Automations">
                <Button
                  type="text"
                  size="small"
                  className="chat-header-icon-action"
                  aria-label="Automations"
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
            {additionalActions}
          </Flex>
        </Flex>
      ) : null}
    </Flex>
  );
}

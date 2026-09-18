import type { ReactNode } from "react";
import { Button, Dropdown, Flex, Typography } from "antd";
import { MoreOutlined } from "@ant-design/icons";
import type { MenuProps } from "antd";
import { formatChatTime } from "./chatTime";

export function ChatHeader({
  title,
  subtitle,
  timestamp,
  sessionsToggle,
  speechLocale,
  onEnd,
  inSession
}: {
  title: string;
  subtitle?: string | null;
  timestamp?: string | null;
  sessionsToggle?: ReactNode;
  speechLocale?: ReactNode;
  onEnd: () => void;
  inSession: boolean;
}) {
  const items: MenuProps["items"] = inSession
    ? [
        {
          key: "end",
          danger: true,
          label: "End",
          onClick: onEnd
        }
      ]
    : [];
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
          {subtitle ? (
            <Typography.Text type="secondary" className="chat-header-subtitle">
              {subtitle}
            </Typography.Text>
          ) : null}
          {speechLocale}
        </div>
      </Flex>
      {inSession ? (
        <Dropdown menu={{ items }} trigger={["click"]} placement="bottomRight">
          <Button type="text" size="small" className="session-overflow" aria-label="Conversation actions" icon={<MoreOutlined />} />
        </Dropdown>
      ) : null}
    </Flex>
  );
}

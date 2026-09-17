import type { ReactNode } from "react";
import { Button, Dropdown, Flex, Typography } from "antd";
import { MoreOutlined } from "@ant-design/icons";
import type { MenuProps } from "antd";

export function ChatHeader({
  title,
  subtitle,
  sessionsToggle,
  onEnd,
  inSession
}: {
  title: string;
  subtitle?: string | null;
  sessionsToggle?: ReactNode;
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

  return (
    <Flex align="center" justify="space-between" gap={12} className="chat-header-inner">
      <Flex align="center" gap={12} className="chat-header-identity">
        {sessionsToggle}
        <div className="chat-header-copy">
          <Typography.Title level={1} className="chat-header-title">
            {title}
          </Typography.Title>
          {subtitle ? (
            <Typography.Text type="secondary" className="chat-header-subtitle">
              {subtitle}
            </Typography.Text>
          ) : null}
        </div>
      </Flex>
      {inSession ? (
        <Dropdown menu={{ items }} trigger={["click"]} placement="bottomRight">
          <Button type="text" aria-label="Conversation actions" icon={<MoreOutlined />} />
        </Dropdown>
      ) : null}
    </Flex>
  );
}

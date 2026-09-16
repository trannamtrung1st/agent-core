import type { ReactNode } from "react";
import { Flex, Tag, Typography } from "antd";

export function Hud({
  profile,
  connectionText,
  connectionTone,
  identity,
  sessionsToggle
}: {
  profile: string;
  connectionText: string;
  connectionTone: "live" | "wait" | "alarm";
  identity: { name: string; role: string } | null;
  sessionsToggle?: ReactNode;
}) {
  const tagColor = connectionTone === "alarm" ? "error" : connectionTone === "wait" ? "warning" : "success";

  return (
    <Flex align="flex-start" gap={16} wrap="wrap" style={{ width: "100%" }}>
      {sessionsToggle}
      <Flex vertical gap={4} flex="1 1 16rem">
        <Typography.Title level={1} style={{ margin: 0, fontSize: 20 }}>
          Agent Core
        </Typography.Title>
        <Typography.Text data-testid="profile">Profile: {profile || "…"}</Typography.Text>
        <Typography.Text data-testid="connection">
          <Tag color={tagColor} style={{ marginInlineEnd: 0 }}>
            {connectionText}
          </Tag>
        </Typography.Text>
        {identity ? (
          <Typography.Text>
            Identity {identity.name || "Agent"} · {identity.role}
          </Typography.Text>
        ) : null}
      </Flex>
    </Flex>
  );
}

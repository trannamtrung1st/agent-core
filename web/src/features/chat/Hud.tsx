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
    <Flex align="center" justify="space-between" gap={12} wrap="wrap" style={{ width: "100%" }}>
      <Flex align="center" gap={12} wrap="wrap">
        {sessionsToggle}
        <Typography.Title level={1} style={{ margin: 0, fontSize: 20 }}>
          Agent Core
        </Typography.Title>
      </Flex>
      <Flex align="center" gap={8} wrap="wrap">
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

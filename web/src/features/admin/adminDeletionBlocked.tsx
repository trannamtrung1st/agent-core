import { Alert, Flex, Typography, theme } from "antd";
import type { ReactNode } from "react";
import { parseAdminDeletionBlockedMessage } from "./adminErrors";

export function AdminDeletionBlockedAlert({
  message,
  className,
  action
}: {
  message: string;
  className?: string;
  action?: ReactNode;
}) {
  const { token } = theme.useToken();
  const parsed = parseAdminDeletionBlockedMessage(message);

  if (!parsed) {
    return (
      <Alert type="error" showIcon className={className} title={message} action={action} />
    );
  }

  return (
    <Alert
      type="error"
      showIcon
      className={className}
      title={parsed.headline}
      action={action}
      description={
        <Flex vertical gap={token.paddingXS} className="admin-deletion-blocked-body">
          <Typography.Text>It is still referenced by:</Typography.Text>
          <ul className="admin-deletion-blocked-list">
            {parsed.bullets.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
          {parsed.footer ? (
            <Typography.Text type="secondary" className="admin-deletion-blocked-footer">
              {parsed.footer}
            </Typography.Text>
          ) : null}
        </Flex>
      }
    />
  );
}

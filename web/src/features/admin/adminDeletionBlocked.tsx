import { Alert, Flex, Typography, theme } from "antd";
import type { ReactNode } from "react";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";
import { parseAdminDeletionBlockedMessage } from "./adminErrors";

export function AdminDeletionBlockedAlert({
  message,
  className,
  action,
  diagnosticId
}: {
  message: string;
  className?: string;
  action?: ReactNode;
  diagnosticId?: string | null;
}) {
  const { token } = theme.useToken();
  const parsed = parseAdminDeletionBlockedMessage(message);
  const actions = diagnosticId ? (
    <Flex gap={token.paddingXS} wrap="wrap" align="center">
      <DiagnosticDetails fields={{ diagnosticId }} />
      {action}
    </Flex>
  ) : action;

  if (!parsed) {
    return (
      <Alert type="error" showIcon className={className} title={message} action={actions} />
    );
  }

  return (
    <Alert
      type="error"
      showIcon
      className={className}
      title={parsed.headline}
      action={actions}
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

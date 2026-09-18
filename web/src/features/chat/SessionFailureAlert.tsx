import type { ReactNode } from "react";
import { Alert, Button, Descriptions, Flex, Popover, Typography } from "antd";
import {
  classLabel,
  resolveSessionError,
  type SessionErrorView
} from "./sessionError";

export function SessionFailureAlert({
  error,
  fatal = false,
  className,
  action
}: {
  error: SessionErrorView | string | null;
  fatal?: boolean;
  className?: string;
  action?: ReactNode;
}) {
  const view = resolveSessionError(error, fatal);
  if (!view) {
    return null;
  }

  const retryHint = view.fatal
    ? "Not retryable from this alert."
    : view.retryAfterMs != null
      ? `Retry possible after ${view.retryAfterMs} ms.`
      : "Retry possible.";
  const items = [
    { key: "class", label: "Class", children: classLabel(view.classId) },
    { key: "category", label: "Category", children: view.category },
    { key: "code", label: "Code", children: view.code },
    { key: "severity", label: "Severity", children: view.fatal ? "Fatal" : "Recoverable" },
    { key: "retry", label: "Retry", children: retryHint }
  ];
  if (view.extensions) {
    for (const [key, value] of Object.entries(view.extensions)) {
      items.push({ key, label: key, children: String(value) });
    }
  }

  const details = (
    <Popover
      trigger="click"
      title="Failure details"
      getPopupContainer={() => document.body}
      content={
        <div data-testid="session-failure-details">
          <Descriptions size="small" column={1} items={items} />
        </div>
      }
    >
      <Button size="small" aria-label="Failure details">
        Details
      </Button>
    </Popover>
  );

  return (
    <Alert
      type="error"
      showIcon
      className={["session-failure", className].filter(Boolean).join(" ")}
      data-testid="session-failure"
      data-error-class={view.classId}
      data-error-code={view.code}
      data-error-fatal={String(view.fatal)}
      title={view.message}
      description={
        <Typography.Text type="secondary">
          {classLabel(view.classId)} · {view.fatal ? "Fatal" : "Recoverable"}
        </Typography.Text>
      }
      action={
        <Flex wrap gap={8} className="session-failure-actions">
          {details}
          {action}
        </Flex>
      }
    />
  );
}

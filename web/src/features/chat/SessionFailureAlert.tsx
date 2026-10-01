import type { ReactNode } from "react";
import { Alert, Button, Flex, Popover, Typography } from "antd";
import { DetailFieldList, type DetailLine } from "./detailFields";
import { DetailPopoverBody } from "./detailPopover";
import { DiagnosticDetails } from "./DiagnosticDetails";
import { detailLinesCopyText } from "./diagnosticCopy";
import {
  classLabel,
  resolveSessionError,
  type SessionErrorView
} from "./sessionError";

function sessionFailureDetailLines(
  view: SessionErrorView,
  options?: { includeCategoryCode?: boolean }
): DetailLine[] {
  const includeCategoryCode = options?.includeCategoryCode ?? true;
  const retryHint = view.fatal
    ? "Not retryable from this alert."
    : view.retryAfterMs != null
      ? `Retry possible after ${view.retryAfterMs} ms.`
      : "Retry possible.";
  const lines: DetailLine[] = [
    { label: "Class", value: classLabel(view.classId) },
    ...(includeCategoryCode
      ? [
          { label: "Category", value: view.category },
          { label: "Code", value: view.code }
        ]
      : []),
    { label: "Severity", value: view.fatal ? "Fatal" : "Recoverable" },
    { label: "Retry", value: retryHint }
  ];
  if (view.extensions) {
    for (const [key, value] of Object.entries(view.extensions)) {
      lines.push({ label: key, value: String(value) });
    }
  }
  return lines;
}

function FailureDetailsPopover({
  lines,
  title,
  ariaLabel
}: {
  lines: DetailLine[];
  title: string;
  ariaLabel: string;
}) {
  const copyText = detailLinesCopyText("Agent Core failure details", lines);

  return (
    <Popover
      trigger="click"
      title={title}
      getPopupContainer={() => document.body}
      content={
        <DetailPopoverBody copyText={copyText}>
          <DetailFieldList items={lines} dataTestId="session-failure-details" />
        </DetailPopoverBody>
      }
    >
      <Button size="small" aria-label={ariaLabel}>
        Details
      </Button>
    </Popover>
  );
}

export function SessionFailureAlert({
  error,
  fatal = false,
  className,
  action,
  sessionId = null
}: {
  error: SessionErrorView | string | null;
  fatal?: boolean;
  className?: string;
  action?: ReactNode;
  sessionId?: string | null;
}) {
  const view = resolveSessionError(error, fatal);
  if (!view) {
    return null;
  }

  const hasDiagnostic = Boolean(view.diagnosticId?.trim());
  const failureLines = sessionFailureDetailLines(view, { includeCategoryCode: !hasDiagnostic });
  const detailsControl = hasDiagnostic ? (
    <DiagnosticDetails
      fields={{
        diagnosticId: view.diagnosticId,
        correlationId: view.correlationId,
        sessionId,
        responseId: view.responseId,
        category: view.category,
        code: view.code,
        failureReason: view.failureReason,
        providerResponseChannel: view.providerResponseChannel
      }}
      extraLines={failureLines}
      failureDetailsTestId="session-failure-details"
      trigger={
        <Button size="small" aria-label="Error details">
          Details
        </Button>
      }
    />
  ) : (
    <FailureDetailsPopover
      lines={failureLines}
      title="Failure details"
      ariaLabel="Failure details"
    />
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
        <Typography.Text type="secondary" className="session-failure-meta">
          {classLabel(view.classId)} · {view.fatal ? "Fatal" : "Recoverable"}
        </Typography.Text>
      }
      action={
        <Flex wrap gap={8} align="center" className="session-failure-actions">
          {detailsControl}
          {action}
        </Flex>
      }
    />
  );
}

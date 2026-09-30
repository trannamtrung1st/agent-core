import { CopyOutlined, InfoCircleOutlined } from "@ant-design/icons";
import { type ReactNode, useState } from "react";
import { Button, Flex, Popover, Typography, theme } from "antd";
import { diagnosticCopyText, type DiagnosticFields } from "./diagnosticCopy";

export function DiagnosticDetails({ fields, trigger }: { fields: DiagnosticFields; trigger?: ReactNode }) {
  const { token } = theme.useToken();
  const copyText = diagnosticCopyText(fields);
  const [copied, setCopied] = useState(false);
  if (!copyText || !fields.diagnosticId?.trim()) {
    return null;
  }

  const diagnosticId = fields.diagnosticId.trim();
  const idStyle = {
    display: "block",
    userSelect: "text" as const,
    fontFamily: token.fontFamilyCode,
    fontSize: token.fontSizeSM,
    wordBreak: "break-all" as const
  };
  async function copy() {
    try {
      await navigator.clipboard.writeText(copyText ?? "");
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  return (
    <Popover
      trigger="click"
      placement="topLeft"
      title="Error details"
      getPopupContainer={() => document.body}
      onOpenChange={(open) => {
        if (!open) {
          setCopied(false);
        }
      }}
      content={
        <Flex
          vertical
          gap={token.paddingXS}
          data-testid="diagnostic-details"
          className="ac-scroll-pane"
          style={{
            width: "min(360px, calc(100vw - 32px))",
            maxHeight: "calc(40vh - 8px)",
            paddingInlineEnd: token.paddingXXS
          }}
        >
          <DiagnosticField label="Diagnostic ID" value={diagnosticId} testId="diagnostic-id" />
          {fields.correlationId?.trim() ? (
            <DiagnosticField label="Correlation ID" value={fields.correlationId.trim()} />
          ) : null}
          {fields.sessionId?.trim() ? (
            <DiagnosticField label="Session ID" value={fields.sessionId.trim()} />
          ) : null}
          {fields.responseId?.trim() ? (
            <DiagnosticField label="Response ID" value={fields.responseId.trim()} />
          ) : null}
          {fields.workItemId?.trim() ? (
            <DiagnosticField label="Work Item ID" value={fields.workItemId.trim()} />
          ) : null}
          {fields.triggerRegistrationId?.trim() ? (
            <DiagnosticField label="Trigger ID" value={fields.triggerRegistrationId.trim()} />
          ) : null}
          {fields.triggerOccurrenceId?.trim() ? (
            <DiagnosticField label="Occurrence ID" value={fields.triggerOccurrenceId.trim()} />
          ) : null}
          {fields.category?.trim() && fields.code?.trim() ? (
            <DiagnosticField label="Error" value={`${fields.category.trim()} / ${fields.code.trim()}`} />
          ) : fields.code?.trim() ? (
            <DiagnosticField label="Error" value={fields.code.trim()} />
          ) : null}
          <Button
            icon={<CopyOutlined />}
            aria-label="Copy diagnostic"
            onClick={() => void copy()}
            style={{ alignSelf: "flex-start", flex: "0 0 auto" }}
          >
            <span role="status">{copied ? "Copied" : "Copy details"}</span>
          </Button>
        </Flex>
      }
    >
      {trigger ?? (
        <Button type="text" icon={<InfoCircleOutlined />} aria-label="Error details" title="Error details" />
      )}
    </Popover>
  );

  function DiagnosticField({ label, value, testId }: { label: string; value: string; testId?: string }) {
    return (
      <div style={{ flex: "0 0 auto" }}>
        <Typography.Text type="secondary" style={{ display: "block", fontSize: token.fontSizeSM }}>
          {label}
        </Typography.Text>
        <Typography.Text style={idStyle} data-testid={testId}>
          {value}
        </Typography.Text>
      </div>
    );
  }
}

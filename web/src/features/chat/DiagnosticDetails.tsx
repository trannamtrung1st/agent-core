import { useState } from "react";
import { Button, Flex, Popover, Typography, theme } from "antd";
import { diagnosticCopyText, type DiagnosticFields } from "./diagnosticCopy";

export function DiagnosticDetails({ fields }: { fields: DiagnosticFields }) {
  const { token } = theme.useToken();
  const copyText = diagnosticCopyText(fields);
  const [copied, setCopied] = useState(false);
  if (!copyText || !fields.diagnosticId?.trim()) {
    return null;
  }

  const diagnosticId = fields.diagnosticId.trim();
  const idStyle = { userSelect: "text" as const, fontFamily: token.fontFamilyCode, wordBreak: "break-all" as const };
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
      title="Error details"
      getPopupContainer={() => document.body}
      onOpenChange={(open) => {
        if (!open) {
          setCopied(false);
        }
      }}
      content={
        <Flex vertical gap={token.paddingXS} data-testid="diagnostic-details">
          <Typography.Text>Agent Core diagnostic</Typography.Text>
          <Typography.Text style={idStyle} data-testid="diagnostic-id">
            Diagnostic ID: {diagnosticId}
          </Typography.Text>
          {fields.correlationId?.trim() ? (
            <Typography.Text style={idStyle}>
              Correlation ID: {fields.correlationId.trim()}
            </Typography.Text>
          ) : null}
          {fields.sessionId?.trim() ? (
            <Typography.Text style={idStyle}>
              Session ID: {fields.sessionId.trim()}
            </Typography.Text>
          ) : null}
          {fields.responseId?.trim() ? (
            <Typography.Text style={idStyle}>
              Response ID: {fields.responseId.trim()}
            </Typography.Text>
          ) : null}
          {fields.workItemId?.trim() ? (
            <Typography.Text style={idStyle}>
              Work Item ID: {fields.workItemId.trim()}
            </Typography.Text>
          ) : null}
          {fields.triggerRegistrationId?.trim() ? (
            <Typography.Text style={idStyle}>
              Trigger ID: {fields.triggerRegistrationId.trim()}
            </Typography.Text>
          ) : null}
          {fields.triggerOccurrenceId?.trim() ? (
            <Typography.Text style={idStyle}>
              Occurrence ID: {fields.triggerOccurrenceId.trim()}
            </Typography.Text>
          ) : null}
          {fields.category?.trim() && fields.code?.trim() ? (
            <Typography.Text>
              Error: {fields.category.trim()} / {fields.code.trim()}
            </Typography.Text>
          ) : fields.code?.trim() ? (
            <Typography.Text>Error: {fields.code.trim()}</Typography.Text>
          ) : null}
          <Button size="small" aria-label="Copy diagnostic" onClick={() => void copy()}>
            Copy
          </Button>
          {copied ? <Typography.Text role="status">Copied</Typography.Text> : null}
        </Flex>
      }
    >
      <Button size="small" aria-label="Error details">
        Error details
      </Button>
    </Popover>
  );
}

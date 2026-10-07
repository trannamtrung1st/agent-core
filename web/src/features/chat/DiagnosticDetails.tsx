import { InfoCircleOutlined } from "@ant-design/icons";
import { type ReactNode, useState } from "react";
import { Button, Popover } from "antd";
import { DetailField, DetailFieldList, type DetailLine } from "./detailFields";
import { DetailPopoverBody } from "./detailPopover";
import { diagnosticCopyText, type DiagnosticFields } from "./diagnosticCopy";

export function DiagnosticDetails({
  fields,
  trigger,
  extraLines = [],
  failureDetailsTestId
}: {
  fields: DiagnosticFields;
  trigger?: ReactNode;
  /** Additional labeled rows in the same layout, included in copy text. */
  extraLines?: DetailLine[];
  failureDetailsTestId?: string;
}) {
  const copyText = diagnosticCopyText(fields, extraLines);
  const [copyResetKey, setCopyResetKey] = useState(0);
  if (!copyText || !fields.diagnosticId?.trim()) {
    return null;
  }

  const diagnosticId = fields.diagnosticId.trim();

  return (
    <Popover
      trigger="click"
      placement="top"
      title="Error details"
      getPopupContainer={() => document.body}
      onOpenChange={(open) => {
        if (!open) {
          setCopyResetKey((value) => value + 1);
        }
      }}
      content={
        <DetailPopoverBody copyText={copyText} dataTestId="diagnostic-details" copyResetKey={copyResetKey}>
          <DetailField label="Diagnostic ID" value={diagnosticId} testId="diagnostic-id" />
          {fields.correlationId?.trim() ? (
            <DetailField label="Correlation ID" value={fields.correlationId.trim()} />
          ) : null}
          {fields.sessionId?.trim() ? (
            <DetailField label="Session ID" value={fields.sessionId.trim()} />
          ) : null}
          {fields.responseId?.trim() ? (
            <DetailField label="Response ID" value={fields.responseId.trim()} />
          ) : null}
          {fields.workItemId?.trim() ? (
            <DetailField label="Work Item ID" value={fields.workItemId.trim()} />
          ) : null}
          {fields.triggerRegistrationId?.trim() ? (
            <DetailField label="Trigger ID" value={fields.triggerRegistrationId.trim()} />
          ) : null}
          {fields.triggerOccurrenceId?.trim() ? (
            <DetailField label="Occurrence ID" value={fields.triggerOccurrenceId.trim()} />
          ) : null}
          {fields.category?.trim() && fields.code?.trim() ? (
            <DetailField label="Error" value={`${fields.category.trim()} / ${fields.code.trim()}`} />
          ) : fields.code?.trim() ? (
            <DetailField label="Error" value={fields.code.trim()} />
          ) : null}
          {fields.failureReason?.trim() ? (
            <DetailField label="Reason" value={fields.failureReason.trim()} testId="diagnostic-reason" />
          ) : null}
          {fields.providerResponseChannel?.trim() ? (
            <DetailField label="Channel" value={fields.providerResponseChannel.trim()} testId="diagnostic-channel" />
          ) : null}
          {fields.protocolRepair === "attempted" ? (
            <DetailField label="Protocol repair" value="attempted" testId="diagnostic-protocol-repair" />
          ) : null}
          {fields.protocolRepairOutcome === "succeeded"
            || fields.protocolRepairOutcome === "failed"
            || fields.protocolRepairOutcome === "cancelled" ? (
            <DetailField label="Repair outcome" value={fields.protocolRepairOutcome} testId="diagnostic-protocol-repair-outcome" />
          ) : null}
          <DetailFieldList items={extraLines} dataTestId={failureDetailsTestId} />
        </DetailPopoverBody>
      }
    >
      {trigger ?? (
        <Button type="text" icon={<InfoCircleOutlined />} aria-label="Error details" title="Error details" />
      )}
    </Popover>
  );
}

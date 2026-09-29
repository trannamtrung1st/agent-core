export type DiagnosticFields = {
  diagnosticId?: string | null;
  correlationId?: string | null;
  sessionId?: string | null;
  responseId?: string | null;
  workItemId?: string | null;
  triggerRegistrationId?: string | null;
  triggerOccurrenceId?: string | null;
  category?: string | null;
  code?: string | null;
};

function present(value: string | null | undefined): string | null {
  const text = value?.trim();
  return text ? text : null;
}

export function diagnosticCopyText(fields: DiagnosticFields): string | null {
  const diagnosticId = present(fields.diagnosticId);
  if (!diagnosticId) {
    return null;
  }

  const lines = ["Agent Core diagnostic", `Diagnostic ID: ${diagnosticId}`];
  const correlationId = present(fields.correlationId);
  const sessionId = present(fields.sessionId);
  const responseId = present(fields.responseId);
  const workItemId = present(fields.workItemId);
  const triggerRegistrationId = present(fields.triggerRegistrationId);
  const triggerOccurrenceId = present(fields.triggerOccurrenceId);
  const category = present(fields.category);
  const code = present(fields.code);
  if (correlationId) {
    lines.push(`Correlation ID: ${correlationId}`);
  }
  if (sessionId) {
    lines.push(`Session ID: ${sessionId}`);
  }
  if (responseId) {
    lines.push(`Response ID: ${responseId}`);
  }
  if (workItemId) {
    lines.push(`Work Item ID: ${workItemId}`);
  }
  if (triggerRegistrationId) {
    lines.push(`Trigger ID: ${triggerRegistrationId}`);
  }
  if (triggerOccurrenceId) {
    lines.push(`Occurrence ID: ${triggerOccurrenceId}`);
  }
  if (category && code) {
    lines.push(`Error: ${category} / ${code}`);
  } else if (code) {
    lines.push(`Error: ${code}`);
  }

  return lines.join("\n");
}

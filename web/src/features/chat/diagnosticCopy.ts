import type { DetailLine } from "./detailFields";

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
  failureReason?: string | null;
  providerResponseChannel?: string | null;
};

export function detailLinesCopyText(header: string, lines: DetailLine[]): string {
  return [header, ...lines.map((line) => `${line.label}: ${line.value}`)].join("\n");
}

const failureReasonTokens = new Set([
  "invalidJson",
  "responseTooLarge",
  "missingDisplayText",
  "invalidSpeech",
  "invalidSpeechMode",
  "missingCustomSpeechText",
  "invalidBlocks",
  "invalidMemory",
  "invalidMemoryProposal",
  "responseFunctionArgumentsInvalid",
  "invalidMarkerEnvelope",
  "modelSuppliedDestination",
  "unknownAction",
  "unknownDisposition",
  "outputLimit",
  "toolCallTruncated"
]);

const responseChannelTokens = new Set([
  "responseFunction",
  "structuredOutput",
  "markerCompatibility",
  "toolCall"
]);

export function allowlistedDiagnosticToken(value: unknown, allowlist: Set<string>): string | null {
  const text = typeof value === "string" ? value.trim() : "";
  return allowlist.has(text) ? text : null;
}

export function allowlistedFailureReason(value: unknown): string | null {
  return allowlistedDiagnosticToken(value, failureReasonTokens);
}

export function allowlistedResponseChannel(value: unknown): string | null {
  return allowlistedDiagnosticToken(value, responseChannelTokens);
}

function present(value: string | null | undefined): string | null {
  const text = value?.trim();
  return text ? text : null;
}

export function diagnosticCopyText(fields: DiagnosticFields, extraLines: DetailLine[] = []): string | null {
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
  const failureReason = present(fields.failureReason);
  const providerResponseChannel = present(fields.providerResponseChannel);
  if (failureReason) {
    lines.push(`Reason: ${failureReason}`);
  }
  if (providerResponseChannel) {
    lines.push(`Channel: ${providerResponseChannel}`);
  }

  for (const line of extraLines) {
    lines.push(`${line.label}: ${line.value}`);
  }

  return lines.join("\n");
}

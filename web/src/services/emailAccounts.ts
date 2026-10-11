import { ownerFetch } from './api';

export type EmailAccountRequest = { requestId: string; expiresAtUtc: string; instanceId: string; agentName: string; expectedInstanceRevision: number;
  choices: Array<{ accountId: string; displayName: string; emailAddress: string; preset: string; accountRef: string | null }> };
export async function getEmailAccountRequest(sessionId: string, signal?: AbortSignal): Promise<EmailAccountRequest | null> {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/email-account-request`, { signal });
  if (!response.ok) throw new Error('Account request could not be loaded. Reconnect and retry.');
  return response.status === 204 ? null : await response.json() as EmailAccountRequest;
}
export async function decideEmailAccount(sessionId: string, request: EmailAccountRequest, accountId: string | null, reference?: string) {
  const response = await ownerFetch(`/api/v2/sessions/${sessionId}/email-account-request/decision`, { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ requestId: request.requestId, accountId, reference, expectedInstanceRevision: request.expectedInstanceRevision }) });
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { detail?: string } | null;
    throw new Error(problem?.detail ?? 'Account selection failed. Refresh the request or send a new user turn.');
  }
  return await response.json() as { status: string; message: string };
}

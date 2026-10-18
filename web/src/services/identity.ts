export type Account = { accountId: string; loginKey: string; displayName: string };
export type AccountContext = { account: Account; systemPrincipalId: string | null; systemPermissions: string[] };
export type Membership = { tenantId: string; name: string; slug: string; userId: string; principalId: string };
export type Page<T> = { items: T[]; total: number; page: number; pageSize: number; revision?: number };
export type Role = { roleId: string; key: string; name: string; isBuiltin: boolean; humanOnly: boolean; permissions: string[] };
export type Permission = { key: string; resourceType: string; humanOnly: boolean; delegatable: boolean; systemOnly: boolean };
export type Member = { userId: string; principalId: string; status: number; displayName: string; login: string; roles: { assignmentId: string; roleId: string; scope: { kind: number; resourceType: string | null; resourceId: string | null } }[] };
export type TenantContext = { tenant: { tenantId: string; name: string; slug: string }; userId: string; principalId: string; permissions: string[]; roles: { roleId: string; name: string }[]; revision: number };
export type InvitationProof = { tenantId: string; invitationId: string; token: string };
export type Invitation = { invitationId: string; tenantId: string; organizationName: string; login: string; expiresAt: string };

const reasons: Record<string, string> = {
  invalid_credentials: 'The email or password is incorrect. Try again.',
  not_authenticated: 'Your session has expired. Sign in again to continue.',
  not_member: 'You no longer have access to this organization. Choose another organization or contact its owner.',
  permission_denied: 'You do not have permission for this action. Contact an organization owner if you need access.',
  approval_unavailable: 'This approval is unavailable or you no longer have access. Return to the inbox or contact an organization owner.',
  last_owner: 'Keep at least one active owner. Add another owner before changing this access.',
  stale_revision: 'Access has changed since you opened this page. Reload the latest access before trying again.',
  stale_account: 'Your account changed in another session. Sign in again before trying this action.',
  invalid_csrf: 'Your security session changed. Reload this page before trying again.',
  invitation_unavailable: 'This invitation is expired, revoked, or already accepted. Ask an organization owner for a new invitation.',
  invitation_pending: 'A pending invitation already exists for this email.',
  invitation_account_mismatch: 'Sign in with the email shown on this invitation.',
  membership_exists: 'This account already has a membership in this organization.',
  human_only: 'This role includes permissions reserved for people. It cannot be assigned to an agent.',
  account_exists: 'An account already uses this email. Sign in to accept the invitation.',
  invalid_request: 'Some values could not be saved. Check the fields and try again.',
  resource_unavailable: 'This item is unavailable or you no longer have access. Return to the list or contact an organization owner.',
  workspace_read_only: 'This workspace is read-only. Reload its current lifecycle and access before making changes.',
  workspace_effect_unknown: 'The file may have changed. Reload current files before deciding whether to try again. No operation was repeated.',
  definition_unavailable: 'This definition version is unavailable. Reload the available versions before choosing again.',
  definition_in_use: 'This definition is used by an Instance or retained conversation history. Keep it and deprecate an unused publication instead.',
  evaluation_required: 'Run each required evaluation before publishing.',
  evaluation_stale: 'The draft changed. Run required evaluations again before publishing.',
  evaluation_failed: 'Resolve the failed required evaluations before publishing.',
  definition_resources_invalid: 'Bind the missing knowledge, Skill or workspace resources before publishing.',
  credential_in_use: 'Remove this credential’s agent bindings before deleting it.',
  oauth_not_configured: 'Ask the operator to configure shared Google OAuth, then reload the credential.',
  oauth_host_disabled: 'Google Drive is disabled on this host. Ask the operator to enable it before connecting.',
  oauth_initiator_unavailable: 'Your original sign-in or organization access is no longer available. Sign in again before connecting.',
  oauth_client_changed: 'Google setup changed. Reload the credential and start a new connection.',
  oauth_callback_unavailable: 'This connection attempt is expired, superseded or unavailable. Return to the saved credential and connect again.',
  oauth_proof_unavailable: 'The connection proof is unavailable. Start a new connection from the saved credential.',
};
export class IdentityError extends Error {
  constructor(public status: number, public reasonCode: string, detail?: string) {
    super(reasons[reasonCode] ?? (status === 400 && detail ? detail.slice(0, 500) : status === 429 ? 'Too many attempts. Wait a minute before trying again.' : status === 404 ? 'This item is no longer available. Return to the list.' : status >= 500 ? 'The service is temporarily unavailable. Check the latest information before retrying.' : 'This action could not be completed. Reload the latest information and try again.'));
  }
}
export class IdentityCompletionError extends Error {
  constructor() { super('The change was saved, but this view could not finish updating. Reload the page before making another change.'); }
}
let csrf: string | null = null;
let pendingCsrf: Promise<string> | null = null;
async function csrfToken(): Promise<string> {
  if (csrf) return csrf;
  if (!pendingCsrf) pendingCsrf = fetch('/api/v2/auth/csrf', { credentials: 'same-origin', cache: 'no-store' })
    .then(async response => { if (!response.ok) throw new IdentityError(response.status, 'csrf_unavailable'); return (await response.json() as { token: string }).token; })
    .then(value => { csrf = value; return value; }).finally(() => { pendingCsrf = null; });
  return pendingCsrf;
}
export function resetIdentityCsrf() { csrf = null; }
export async function identityRequest<T>(path: string, options: { method?: string; body?: unknown; signal?: AbortSignal } = {}): Promise<T> {
  const method = options.method ?? 'GET';
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  if (method !== 'GET') headers['X-AgentCore-CSRF'] = await csrfToken();
  options.signal?.throwIfAborted();
  const response = await fetch(path, { method, headers, credentials: 'same-origin', cache: 'no-store', signal: options.signal, body: options.body === undefined ? undefined : JSON.stringify(options.body) });
  await requireIdentityResponse(response, path);
  if (response.status === 204) return undefined as T;
  try { return await response.json() as T; }
  catch (error) { if (method !== 'GET' && !options.signal?.aborted) throw new IdentityCompletionError(); throw error; }
}
async function requireIdentityResponse(response: Response, path: string) {
  if (!response.ok) {
    const problem = await response.json().catch(() => ({})) as { reasonCode?: string; title?: string };
    if (problem.reasonCode === 'invalid_csrf') resetIdentityCsrf();
    if (response.status === 401 && !path.startsWith('/api/v2/auth/')) window.dispatchEvent(new Event('agentcore:authentication-expired'));
    throw new IdentityError(response.status, problem.reasonCode ?? 'request_failed', problem.title);
  }
}
export async function identityBlob(path: string, signal?: AbortSignal): Promise<Blob> {
  const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store', signal });
  await requireIdentityResponse(response, path); return response.blob();
}
/** Sends supplied file bytes once. An acknowledgement carries no read authority. */
export async function identityFileMutation(path: string, method: 'PUT' | 'DELETE', body: Blob | undefined, signal: AbortSignal): Promise<void> {
  const token = await csrfToken(); signal.throwIfAborted();
  const response = await fetch(path, { method, body, signal, credentials: 'same-origin', cache: 'no-store',
    headers: { 'X-AgentCore-CSRF': token, ...(body ? { 'Content-Type': 'application/octet-stream' } : {}) } });
  await requireIdentityResponse(response, path);
  if (response.status !== 204) throw new IdentityError(409, 'workspace_effect_unknown');
}
/** Raw authenticated GET for bounded file readers which own safe source-specific error parsing. */
export async function identityReadResponse(path: string, signal: AbortSignal): Promise<Response> {
  const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store', signal });
  if (response.status === 401) window.dispatchEvent(new Event('agentcore:authentication-expired'));
  return response;
}
export const tenantApi = (tenantId: string, suffix: string) => `/api/v2/tenants/${encodeURIComponent(tenantId)}${suffix}`;
export const membershipsApi = (page: number, q = '') => `/api/v2/account/tenants?page=${page}&pageSize=10&q=${encodeURIComponent(q)}`;

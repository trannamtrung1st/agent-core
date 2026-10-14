import { ownerFetch } from './api';
import { adminProblemMessage, type SystemCredential } from './adminApi';
export type DriveProfile = {
  emailAddress: string;
  connectionStatus: string;
  authEpoch: number;
  connectedAtUtc: string | null;
  synthetic: boolean;
};
export type DriveConnection = {
  status: string;
  configured: boolean;
  synthetic: boolean;
  error: string | null;
  lastCheckedAtUtc: string | null;
  providerRevoked: boolean | null;
};
export type DriveOptions = {
  enabled: boolean;
  configured: boolean;
  synthetic: boolean;
  scope: string;
};
export type DriveAccount = {
  accountRef: string;
  displayName: string;
  emailAddress: string;
  status: string;
  authEpoch: number;
  available: boolean;
  recovery: string | null;
  bindingId: string;
  synthetic: boolean;
};
export type DriveFile = {
  id: string;
  name: string;
  mimeType: string;
  byteSize: number;
  version: string;
  md5Checksum: string | null;
  parents: string[];
  trashed: boolean;
  webViewLink: string | null;
  folder: boolean;
  nativeDocument: boolean;
};
export type DrivePage = {
  items: DriveFile[];
  nextCursor: string | null;
  limitedToAppAuthorizedFiles: boolean;
};
export type DriveReceipt = {
  operationId: string;
  status: string;
  file: DriveFile;
  reconciled: boolean;
  synthetic: boolean;
  limitation: string | null;
};
async function request<T>(
  path: string,
  body?: unknown,
  signal?: AbortSignal,
): Promise<T> {
  const response = await ownerFetch(path, {
    signal,
    ...(body === undefined
      ? {}
      : {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body),
        }),
  });
  if (!response.ok)
    throw await adminProblemMessage(
      response,
      'Drive operation failed. Check the account and retry reading its state.',
    );
  return (await response.json()) as T;
}
let options: Promise<DriveOptions> | null = null;
export function getDriveOptions() {
  if (!options) {
    const pending = request<DriveOptions>(
      '/api/v2/integrations/google-drive/options',
    );
    options = pending;
    void pending.catch(() => {
      if (options === pending) options = null;
    });
  }
  return options;
}
const credential = (id: string) =>
  `/api/v2/admin/credentials/${encodeURIComponent(id)}`;
export const createDriveAccount = (displayName: string) =>
  request<SystemCredential>('/api/v2/admin/credentials/google-drive', {
    displayName,
  });
export const getDriveConnection = (c: SystemCredential) =>
  request<DriveConnection>(`${credential(c.credentialId)}/drive-connection`);
export const startDriveConnection = (c: SystemCredential) =>
  request<{ authorizationUrl: string; synthetic: boolean }>(
    `${credential(c.credentialId)}/drive-connect`,
    { expectedRevision: c.revision },
  );
export const checkDriveConnection = (c: SystemCredential) =>
  request<DriveConnection>(`${credential(c.credentialId)}/drive-check`, {
    expectedRevision: c.revision,
  });
export const disconnectDriveConnection = (c: SystemCredential) =>
  request<DriveConnection>(`${credential(c.credentialId)}/drive-disconnect`, {
    expectedRevision: c.revision,
  });
const root = (instance: string, account: string) =>
  `/api/v2/agent-instances/${encodeURIComponent(instance)}/drive/accounts/${encodeURIComponent(account)}`;
export const getDriveAccounts = (instance: string, signal?: AbortSignal) =>
  request<DriveAccount[]>(
    `/api/v2/agent-instances/${encodeURIComponent(instance)}/drive/accounts`,
    undefined,
    signal,
  );
export function listDriveFiles(
  instance: string,
  account: string,
  folder?: string,
  cursor?: string,
  query?: string,
  signal?: AbortSignal,
) {
  const params = new URLSearchParams({
    limit: '20',
    ...(folder ? { folderId: folder } : {}),
    ...(cursor ? { cursor } : {}),
    ...(query ? { query } : {}),
  });
  return request<DrivePage>(
    `${root(instance, account)}/files?${params}`,
    undefined,
    signal,
  );
}
export const inspectDriveOperation = (
  instance: string,
  account: string,
  operationId: string,
  signal?: AbortSignal,
) =>
  request<DriveReceipt>(
    `${root(instance, account)}/operations/${encodeURIComponent(operationId)}`,
    undefined,
    signal,
  );
export function safeDriveLink(link: string | null) {
  if (!link) return null;
  try {
    const url = new URL(link);
    return url.protocol === 'https:' &&
      ['drive.google.com', 'docs.google.com'].includes(url.hostname) &&
      !url.username &&
      !url.password
      ? url.href
      : null;
  } catch {
    return null;
  }
}

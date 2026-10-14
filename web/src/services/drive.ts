import { ownerFetch } from './api';
import { adminProblemMessage, type SystemCredential } from './adminApi';
import { sourcePath, type FileRef } from '../features/filePreview/fileSource';
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
export class DrivePendingError extends Error {
  constructor(
    public operationId: string,
    public fileId: string,
    public resumeAllowed = false,
  ) {
    super(
      resumeAllowed
        ? 'Upload completion is uncertain. Check the operation or continue this upload. The same file and destination are kept.'
        : 'Upload completion is uncertain. Check the operation and confirm account access in Drive. Do not create another file while this outcome is unknown.',
    );
  }
}
export class DriveOperationFailedError extends Error {}
async function request<T>(
  path: string,
  body?: unknown,
  signal?: AbortSignal,
): Promise<T> {
  const operationId =
    path.endsWith('/drive-upload') &&
    body &&
    typeof body === 'object' &&
    'operationId' in body &&
    typeof body.operationId === 'string'
      ? body.operationId
      : null;
  let response: Response;
  try {
    response = await ownerFetch(path, {
      signal,
      ...(body === undefined
        ? {}
        : {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body),
          }),
    });
  } catch (reason) {
    if (operationId && !signal?.aborted)
      throw new DrivePendingError(operationId, '');
    throw reason;
  }
  if (!response.ok) {
    const raw = await response
      .clone()
      .json()
      .catch(() => null);
    if (raw?.error === 'drive_effect_indeterminate')
      throw new DrivePendingError(
        raw.operationId,
        raw.fileId,
        raw.resumeAllowed === true,
      );
    if (raw?.title === 'drive_operation_failed')
      throw new DriveOperationFailedError(
        raw.detail ??
          'This operation did not execute. Resolve the cause, then start a new save.',
      );
    if (operationId && response.status >= 500)
      throw new DrivePendingError(operationId, '');
    throw await adminProblemMessage(
      response,
      'Drive operation failed. Check the account and retry reading its state.',
    );
  }
  try {
    return (await response.json()) as T;
  } catch (reason) {
    if (operationId && !signal?.aborted)
      throw new DrivePendingError(operationId, '');
    throw reason;
  }
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
export const getSaveAccounts = (file: FileRef, signal?: AbortSignal) =>
  request<{ instanceId: string; accounts: DriveAccount[] }>(
    `${sourcePath(file)}/drive-accounts`,
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
export const saveToDrive = (
  file: FileRef,
  body: {
    accountRef: string;
    authEpoch: number;
    bindingId: string;
    expectedSha256: string;
    operationId: string;
    folderId: string;
    name: string;
  },
  signal?: AbortSignal,
) => request<DriveReceipt>(`${sourcePath(file)}/drive-upload`, body, signal);
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

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

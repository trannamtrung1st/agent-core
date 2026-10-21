import { PreviewError } from './fileSource';
import { identityReadResponse, tenantApi } from '../../services/identity';
export type PreviewOptions = {
  limits: { maxBytes: number; textBytes: number; delimitedBytes: number; docxBytes: number; xlsxBytes: number; imageBytes: number; audioBytes: number; videoBytes: number };
  text: { maxLines: number; highlightMaxBytes: number; highlightMaxLines: number };
  image: { maxPixels: number; maxDimension: number };
  spreadsheet: { rowsPerWindow: number; columnsPerWindow: number; maxCellsPerWindow: number; csvMaxParsedRows: number; csvMaxParsedColumns: number; maxCellCharacters: number; maxWindowCharacters: number };
  workerTimeoutMs: number;
};
// Each owner reads current cookie-authorized policy; failures remain explicitly retryable.
export function loadPreviewOptions(tenant: string, signal?: AbortSignal): Promise<PreviewOptions> {
  return fetchPolicy(tenant, signal);
}
async function fetchPolicy(tenant: string, signal?: AbortSignal): Promise<PreviewOptions> {
  const bounded = signal ? AbortSignal.any([signal, AbortSignal.timeout(10000)]) : AbortSignal.timeout(10000);
  const response = await identityReadResponse(tenantApi(tenant, '/file-preview/options'), bounded);
  if (!response.ok) throw new PreviewError('preview_policy_unavailable', 'Preview settings could not be loaded. Retry or download the original.');
  const policy = await response.json() as PreviewOptions;
  if (!validPreviewOptions(policy)) throw new PreviewError('preview_policy_unavailable', 'Preview settings are unavailable. Retry or download the original.');
  return policy;
}
export function validPreviewOptions(policy: PreviewOptions): boolean {
  const groups = [policy?.limits, policy?.text, policy?.image, policy?.spreadsheet];
  const keys = [
    ['maxBytes', 'textBytes', 'delimitedBytes', 'docxBytes', 'xlsxBytes', 'imageBytes', 'audioBytes', 'videoBytes'],
    ['maxLines', 'highlightMaxBytes', 'highlightMaxLines'], ['maxPixels', 'maxDimension'],
    ['rowsPerWindow', 'columnsPerWindow', 'maxCellsPerWindow', 'csvMaxParsedRows', 'csvMaxParsedColumns', 'maxCellCharacters', 'maxWindowCharacters'],
  ];
  return groups.every((group, i) => group && keys[i].every(key => Number.isSafeInteger((group as Record<string, number>)[key]) && (group as Record<string, number>)[key] > 0))
    && Number.isSafeInteger(policy.workerTimeoutMs) && policy.workerTimeoutMs > 0
    && policy.spreadsheet.rowsPerWindow * policy.spreadsheet.columnsPerWindow <= policy.spreadsheet.maxCellsPerWindow
    && Object.values(policy.limits).every(limit => limit <= policy.limits.maxBytes);
}
export const mib = (bytes: number) => `${(bytes / 1048576).toLocaleString(undefined, { maximumFractionDigits: 2 })} MiB`;

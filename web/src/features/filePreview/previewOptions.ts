import { ownerFetch } from '../../services/api';
import { PreviewError } from './fileSource';
export type PreviewOptions = {
  limits: { maxBytes: number; textBytes: number; delimitedBytes: number; docxBytes: number; xlsxBytes: number; imageBytes: number; audioBytes: number; videoBytes: number };
  text: { maxLines: number; highlightMaxBytes: number; highlightMaxLines: number };
  image: { maxPixels: number; maxDimension: number };
  spreadsheet: { rowsPerWindow: number; columnsPerWindow: number; maxCellsPerWindow: number; csvMaxParsedRows: number; csvMaxParsedColumns: number; maxCellCharacters: number; maxWindowCharacters: number };
  workerTimeoutMs: number;
};
let pending: Promise<PreviewOptions> | null = null;
// Safe host policy is cached for this application load. Failed reads remain retryable.
export function loadPreviewOptions(): Promise<PreviewOptions> {
  if (!pending) pending = fetchPolicy().catch(error => { pending = null; throw error instanceof PreviewError ? error : new PreviewError('preview_policy_unavailable', 'Preview settings could not be loaded. Retry or download the original.'); });
  return pending;
}
async function fetchPolicy(): Promise<PreviewOptions> {
  const response = await ownerFetch('/api/v2/file-preview/options', { signal: AbortSignal.timeout(10000) });
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

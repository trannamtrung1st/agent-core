import { ownerFetch } from '../../services/api';
import {
  PreviewError,
  readFailure,
  sourcePath,
  driveReadQuery,
  type FileRef,
} from './fileSource';
export type OfficeCell = {
  address: string;
  type: string;
  value: string;
  formula: string | null;
  valueStatus: string;
  numberFormat: string;
};
export type OfficePreview = {
  format: 'docx' | 'xlsx';
  emptyBody?: boolean;
  sha256: string;
  warnings: string[];
  sheets?: string[];
  sheet?: string;
  range?: string;
  rows?: OfficeCell[][];
};
export async function readOfficePreview(
  file: FileRef,
  signal: AbortSignal,
  sheet?: string,
  range?: string,
): Promise<OfficePreview> {
  const query = new URLSearchParams(
    file.kind === 'drive' ? driveReadQuery(file) : '',
  );
  const selection = new URLSearchParams({
    ...(sheet ? { sheet } : {}),
    ...(range ? { range } : {}),
  });
  selection.forEach((value, key) => query.set(key, value));
  const response = await ownerFetch(
    `${sourcePath(file)}/office-preview${query.size ? '?' + query : ''}`,
    { signal },
  );
  if (!response.ok) {
    if (response.status === 400)
      throw new PreviewError(
        'invalid_office_package',
        'This Office file or selected range is unsupported, unsafe or corrupt. Download the original or select a smaller range.',
      );
    if (response.status === 413)
      throw new PreviewError(
        'preview_too_large',
        'The Office file exceeds 10 MiB. Download the original.',
      );
    throw readFailure(response.status);
  }
  const result = (await response.json()) as OfficePreview;
  if (
    !['docx', 'xlsx'].includes(result.format) ||
    !/^[a-f0-9]{64}$/i.test(result.sha256) ||
    !Array.isArray(result.warnings) ||
    (result.format === 'xlsx' &&
      (!Array.isArray(result.sheets) ||
        result.sheets.length > 50 ||
        !Array.isArray(result.rows) ||
        result.rows.length > 200 ||
        result.rows.some(
          (row) =>
            !Array.isArray(row) ||
            row.length > 64 ||
            row.some(
              (cell) =>
                typeof cell.value !== 'string' ||
                typeof cell.address !== 'string' ||
                typeof cell.valueStatus !== 'string',
            ),
        )))
  )
    throw new PreviewError(
      'invalid_office_package',
      'Office preview data is unavailable. Retry or download the original.',
    );
  return result;
}

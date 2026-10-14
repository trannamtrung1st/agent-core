import type { PreviewOptions } from './previewOptions';
import { OwnerCapabilityError, ownerFetch } from '../../services/api';
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
  style?: OfficeCellStyle;
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
  presentation?: OfficeWorksheet;
};
export async function readOfficePreview(
  file: FileRef,
  signal: AbortSignal,
  policy: PreviewOptions,
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
  ).catch(reason => {
    if (reason instanceof OwnerCapabilityError) throw readFailure(403);
    throw reason;
  });
  if (!response.ok) {
    if (response.status === 400)
      throw new PreviewError(
        'invalid_office_package',
        'This Office file or selected range is unsupported, unsafe or corrupt. Download the original or select a smaller range.',
      );
    if (response.status === 413) {
      const problem = await response.json().catch(() => ({}));
      throw new PreviewError(problem.title ?? 'preview_too_large', problem.detail ?? 'The file or selected range exceeds its preview limit. Choose a smaller range or download the original.');
    }
    throw readFailure(response.status);
  }
  const result = (await response.json()) as OfficePreview;
  if (
    !['docx', 'xlsx'].includes(result.format) ||
    !/^[a-f0-9]{64}$/i.test(result.sha256) ||
    !Array.isArray(result.warnings) ||
    (result.format === 'xlsx' &&
      (!Array.isArray(result.sheets) ||
        result.sheets.length === 0 ||
        !Array.isArray(result.rows) ||
        result.rows.length > policy.spreadsheet.rowsPerWindow ||
        result.rows.some(
          (row) =>
            !Array.isArray(row) ||
            row.length > policy.spreadsheet.columnsPerWindow ||
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

export type OfficeBorder = { style: string; color: string | null };
export type OfficeCellStyle = { fontFamily: string; fontSize: number; bold: boolean; italic: boolean; fontColor: string | null; fillColor: string | null; horizontal: string; vertical: string; wrap: boolean; top: OfficeBorder; right: OfficeBorder; bottom: OfficeBorder; left: OfficeBorder };
export type OfficeWorksheet = { firstRow: number; firstColumn: number; totalRows: number; totalColumns: number; rowDimensions: { pixels: number; hidden: boolean }[]; columnDimensions: { pixels: number; hidden: boolean }[]; merges: { row: number; column: number; rows: number; columns: number; clipped: boolean }[]; frozenRows: number; frozenColumns: number };

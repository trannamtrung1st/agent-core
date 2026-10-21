import type { PreviewOptions } from './previewOptions';
import {
  PreviewError,
  readFailure,
  sourcePath,
  type FileRef,
  fileReadResponse,
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
  sourceBase64?: string;
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
    file.kind === 'tenantWorkspace' ? new URLSearchParams({path: file.path}).toString() : '',
  );
  const selection = new URLSearchParams({
    ...(sheet ? { sheet } : {}),
    ...(range ? { range } : {}),
  });
  selection.forEach((value, key) => query.set(key, value));
  const response = await fileReadResponse(
    `${sourcePath(file)}/office-preview${query.size ? '?' + query : ''}`,
    signal,
  );
  if (!response.ok) {
    if (response.status === 400 || response.status === 413) {
      const code = await readProblemCode(response, signal);
      if (code && Object.hasOwn(officeRecovery, code))
        throw new PreviewError(code, officeRecovery[code as keyof typeof officeRecovery]);
      if (response.status === 413)
        throw new PreviewError('preview_too_large', officeRecovery.preview_too_large);
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

const officeRecovery = {
  invalid_office_package: 'This Office file may be malformed or unsafe. Download the original to inspect it.',
  office_feature_unsupported: 'This Office file contains a feature the preview does not support. The original file is still available to download.',
  office_operation_unsupported: 'This preview operation is unsupported. Choose a concrete worksheet range where applicable. The original file is still available to download.',
  office_processing_limit: 'This Office file exceeds the preview processing limit. Try a smaller range where applicable or download the original.',
  preview_structure_limit: 'This file or selected range exceeds the preview structure limit. Choose a smaller range where applicable or download the original.',
  preview_too_large: 'This Office file exceeds its preview byte limit. Download the original.',
};

// Problem bodies and their codes are untrusted. Bound the stream before parsing;
// only local recovery copy is displayed, including for unknown/malformed problems.
async function readProblemCode(response: Response, signal: AbortSignal): Promise<string | null> {
  const reader = response.body?.getReader();
  if (!reader) return null;
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      signal.throwIfAborted();
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > 4096) return null;
      chunks.push(value);
    }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    const problem: unknown = JSON.parse(new TextDecoder().decode(bytes));
    if (problem && typeof problem === 'object' && 'title' in problem && typeof problem.title === 'string' && problem.title.length <= 64)
      return problem.title;
    return null;
  } catch {
    signal.throwIfAborted();
    return null;
  } finally {
    await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}

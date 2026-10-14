import type { ComponentProps } from 'react';
import type { Workbook } from '@fortune-sheet/react';
import type { OfficeBorder, OfficeCell, OfficeWorksheet } from './officeSource';
import type { PreviewOptions } from './previewOptions';
export type ReadonlySheet = ComponentProps<typeof Workbook>['data'][number];
const color = (value: string | null) => value && /^#[0-9a-f]{6}$/i.test(value) ? value : undefined;
const borderStyles: Record<string, number> = { Thin: 1, Hair: 2, Dotted: 3, Dashed: 4, DashDot: 5, DashDotDot: 6, Double: 7, Medium: 8, MediumDashed: 9, MediumDashDot: 10, MediumDashDotDot: 11, SlantDashDot: 12, Thick: 13 };
const border = (b: OfficeBorder) => borderStyles[b.style] ? { style: borderStyles[b.style], color: color(b.color) ?? '#000000' } : undefined;
export const columnName = (column: number): string => {
  let name = '';
  while (column > 0) { column--; name = String.fromCharCode(65 + column % 26) + name; column = Math.floor(column / 26); }
  return name;
};
// This is a presentation adapter, never an Excel/formula import. All v/m values are literal strings.
export function spreadsheetSheet(name: string, rows: OfficeCell[][], limits: PreviewOptions['spreadsheet'], presentation?: OfficeWorksheet, header = false): { sheet: ReadonlySheet; truncated: boolean } {
  const width = Math.min(limits.columnsPerWindow, Math.max(1, ...rows.map(row => row.length)));
  const height = Math.min(limits.rowsPerWindow, rows.length, Math.floor(limits.maxCellsPerWindow / width));
  let remaining = limits.maxWindowCharacters;
  let truncated = height < rows.length;
  const borderInfo: unknown[] = [];
  const data = rows.slice(0, height).map((row, r) => Array.from({ length: width }, (_, c) => {
    const source = row[c];
    const text = source?.value ?? '';
    const length = Math.max(0, Math.min(limits.maxCellCharacters, remaining));
    const value = text.length > length ? text.slice(0, Math.max(0, length - 1)) + (length ? '…' : '') : text;
    remaining -= value.length;
    truncated ||= value !== text;
    const s = source?.style;
    if (s) borderInfo.push({ rangeType: 'cell', value: { row_index: r, col_index: c, t: border(s.top), r: border(s.right), b: border(s.bottom), l: border(s.left) } });
    return { v: value, m: value, ct: { t: 's', fa: '@' },
      ...(header && r === 0 ? { bl: 1, bg: '#F3F4F6' } : {}),
      ...(s ? { ff: /^[\p{L}\p{N} ,._-]{1,80}$/u.test(s.fontFamily) ? s.fontFamily : 'Arial', fs: Math.max(1, Math.min(72, s.fontSize)), bl: s.bold ? 1 : 0, it: s.italic ? 1 : 0,
        fc: color(s.fontColor), bg: color(s.fillColor), ht: s.horizontal === 'Center' ? 0 : s.horizontal === 'Right' || s.horizontal === 'General' && ['Number', 'DateTime'].includes(source.type) ? 2 : 1,
        vt: s.vertical === 'Top' ? 1 : s.vertical === 'Center' ? 0 : 2, tb: s.wrap ? '2' : '1' } : {}),
    };
  }));
  const config: NonNullable<ReadonlySheet['config']> = { merge: {}, rowlen: {}, columnlen: {}, rowhidden: {}, colhidden: {}, borderInfo };
  presentation?.rowDimensions.slice(0, height).forEach((d, r) => { config.rowlen![r] = d.pixels; if (d.hidden) config.rowhidden![r] = 0; });
  presentation?.columnDimensions.slice(0, width).forEach((d, c) => { config.columnlen![c] = d.pixels; if (d.hidden) config.colhidden![c] = 0; });
  for (const merge of presentation?.merges ?? []) {
    const { row: r, column: c, rows: rs, columns: cs } = merge;
    if (merge.clipped || r < 0 || c < 0 || r + rs > height || c + cs > width) continue;
    const mc = { r, c, rs, cs }; config.merge![`${r}_${c}`] = mc;
    for (let y = r; y < r + rs; y++) for (let x = c; x < c + cs; x++) {
      Object.assign(data[y][x], { mc: y === r && x === c ? mc : { r, c } });
    }
  }
  const frozenRows = presentation?.frozenRows ?? (header ? 1 : 0);
  const frozenColumns = presentation?.frozenColumns ?? 0;
  return { sheet: { id: 'preview', name, status: 1, row: Math.max(1, height), column: width, data, celldata: data.flatMap((row, r) => row.map((v, c) => ({ r, c, v }))), config, addRows: 0,
    ...(frozenRows || frozenColumns ? { frozen: { type: frozenRows && frozenColumns ? 'rangeBoth' : frozenRows ? 'rangeRow' : 'rangeColumn', range: { row_focus: frozenRows - 1, column_focus: frozenColumns - 1 } } } : {}),
  }, truncated };
}

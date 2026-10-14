import { describe, expect, it } from 'vitest';
import { spreadsheetSheet } from './spreadsheetAdapter';
import { testPolicy } from './previewTestPolicy';
import { parseDelimited } from './parseDelimited';
const cells = (values: string[][]) => values.map(row => row.map(value => ({ address: 'A1', value, type: 'Text', formula: '=WEBSERVICE("https://invalid.test")', valueStatus: 'cached_value_stale', numberFormat: '@' })));
describe('Inert window projection', () => {
  it('keeps literal values and excludes all active formulas and links', () => {
    const values = ['00123', '9007199254740993', '=HYPERLINK("evil")', '+1', '-1', '@name', '2026-10-14'];
    const { sheet } = spreadsheetSheet('CSV', cells([values]), testPolicy.spreadsheet);
    expect(sheet.data?.[0].map(cell => cell?.v)).toEqual(values);
    expect(JSON.stringify(sheet)).not.toContain('WEBSERVICE');
    expect(sheet.hyperlink).toBeUndefined(); expect(sheet.calcChain).toBeUndefined();
  });
  it('bounds tall/wide/long windows and never invents clipped merges', () => {
    const limits = { ...testPolicy.spreadsheet, rowsPerWindow: 2, columnsPerWindow: 2, maxCellsPerWindow: 4, maxCellCharacters: 5, maxWindowCharacters: 8 };
    const { sheet, truncated } = spreadsheetSheet('Bounded', cells([['1234567','abcdef'], ['third','fourth'], ['last']]), limits, { firstRow: 2, firstColumn: 2, totalRows: 9, totalColumns: 8,
      rowDimensions: [{ pixels: 48, hidden: false }, { pixels: 20, hidden: true }], columnDimensions: [{ pixels: 100, hidden: false }, { pixels: 50, hidden: true }],
      merges: [{ row: 0, column: 0, rows: 1, columns: 2, clipped: true }], frozenRows: 0, frozenColumns: 0 });
    expect(truncated).toBe(true); expect(sheet.data).toHaveLength(2); expect(sheet.data?.every(row => row.length === 2)).toBe(true);
    expect(sheet.data?.flat().reduce((n, cell) => n + String(cell?.v ?? '').length, 0)).toBeLessThanOrEqual(8);
    expect(sheet.config?.merge).toEqual({}); expect(sheet.config?.rowhidden).toEqual({ 1: 0 });
  });
  it('parses to configured row/column limits and reports partial data', () => {
    const result = parseDelimited('a,b,c\n001,=CMD(),x\n002,003,y', ',', { ...testPolicy.spreadsheet, csvMaxParsedRows: 2, csvMaxParsedColumns: 2 });
    expect(result.rows).toEqual([['a','b'], ['001','=CMD()']]); expect(result.partial).toBe(true);
  });
});

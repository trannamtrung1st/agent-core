import type { PreviewOptions } from './previewOptions';
import { parse } from 'papaparse';
export function parseDelimited(source: string, delimiter: string, limits: PreviewOptions['spreadsheet']) {
  const rows: string[][] = [];
  let partial = false;
  let columns = 0;
  let malformed = false;
  parse<string[]>(source.replace(/^\uFEFF/, ''), {
    delimiter, dynamicTyping: false, skipEmptyLines: false,
    step(result, parser) {
      if (result.errors.some(error => error.code !== 'UndetectableDelimiter')) { malformed = true; parser.abort(); return; }
      if (rows.length >= limits.csvMaxParsedRows) { partial = true; parser.abort(); return; }
      if (result.data.length > limits.csvMaxParsedColumns) partial = true;
      columns = Math.max(columns, Math.min(result.data.length, limits.csvMaxParsedColumns));
      rows.push(result.data.slice(0, limits.csvMaxParsedColumns));
    },
  });
  if (malformed) throw new Error('The delimited file is malformed. Read Text or download the original.');
  return { rows, partial, columns };
}

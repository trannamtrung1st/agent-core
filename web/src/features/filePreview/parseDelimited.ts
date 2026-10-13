import { previewLimits } from './previewPolicy';
import { parse } from 'papaparse';
export function parseDelimited(source: string, delimiter: string) {
  const result = parse<string[]>(source.replace(/^\uFEFF/, ''), {
    delimiter,
    dynamicTyping: false,
    skipEmptyLines: false,
    preview: previewLimits.rows + 1,
  });
  if (result.errors.some((error) => error.code !== 'UndetectableDelimiter'))
    throw new Error(
      'The delimited file is malformed. Read Text or download the original.',
    );
  if (result.data.some((row) => row.length > previewLimits.columns))
    throw new Error(
      'This file exceeds 256 preview columns. Read Text or download the original.',
    );
  return {
    rows: result.data.slice(0, previewLimits.rows),
    partial: result.meta.truncated || result.data.length > previewLimits.rows,
  };
}

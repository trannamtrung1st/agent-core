// Test-only policy fixture. Keep assertions aligned with backend defaults.
import type { PreviewOptions } from './previewOptions';
export const testPolicy: PreviewOptions = {
  limits: { maxBytes: 33554432, textBytes: 2097152, delimitedBytes: 5242880, docxBytes: 10485760, xlsxBytes: 10485760, imageBytes: 15728640, audioBytes: 33554432, videoBytes: 33554432 },
  text: { maxLines: 10000, highlightMaxBytes: 262144, highlightMaxLines: 3000 },
  image: { maxPixels: 40000000, maxDimension: 32768 },
  spreadsheet: { rowsPerWindow: 200, columnsPerWindow: 64, maxCellsPerWindow: 12800, csvMaxParsedRows: 50000, csvMaxParsedColumns: 256, maxCellCharacters: 4096, maxWindowCharacters: 64000 },
  workerTimeoutMs: 5000,
};

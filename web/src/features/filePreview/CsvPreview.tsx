import { lazy, Suspense, useEffect, useMemo, useState } from 'react';
import { Alert, Button, Checkbox, Flex, Pagination, Segmented, Spin, Typography, theme } from 'antd';
import { extension } from './previewPolicy';
import type { PreviewOptions } from './previewOptions';
import { TextPreview } from './TextPreview';
import type { parseDelimited } from './parseDelimited';
import { processPreview } from './previewWorker';
import { columnName, spreadsheetSheet } from './spreadsheetAdapter';
export function CsvPreview({ source, name, tabSeparated = false, policy }: { source: string; name: string; tabSeparated?: boolean; policy: PreviewOptions }) {
  const ReadonlySpreadsheet = useMemo(() => lazy(() => import('./ReadonlySpreadsheet').then(module => ({ default: module.ReadonlySpreadsheet }))), []);
  const { token } = theme.useToken();
  const [mode, setMode] = useState('Grid'), [headers, setHeaders] = useState(policy.spreadsheet.rowsPerWindow > 1), [page, setPage] = useState(1), [columnPage, setColumnPage] = useState(1);
  const [result, setResult] = useState<ReturnType<typeof parseDelimited> | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const controller = new AbortController(); setResult(null); setError(null);
    if (mode !== 'Grid') return;
    void processPreview<ReturnType<typeof parseDelimited>>('csv', source, tabSeparated || extension(name) === 'tsv' ? '\t' : ',', controller.signal, policy)
      .then(value => { if (!controller.signal.aborted) setResult(value); })
      .catch(e => { if (!controller.signal.aborted) setError(e.message); });
    return () => controller.abort();
  }, [source, name, tabSeparated, policy, mode]);
  const limits = policy.spreadsheet;
  const pageRows = Math.max(1, limits.rowsPerWindow - (headers ? 1 : 0));
  const bodyCount = Math.max(0, (result?.rows.length ?? 0) - (headers ? 1 : 0));
  const start = (page - 1) * pageRows + (headers ? 1 : 0), column = (columnPage - 1) * limits.columnsPerWindow;
  const cells = useMemo(() => {
    const rows = result?.rows ?? [];
    const window = rows.slice(start, start + pageRows);
    if (headers && rows.length) window.unshift(rows[0]);
    const width = Math.max(1, Math.min(limits.columnsPerWindow, (result?.columns ?? 1) - column));
    return window.map((row, r) => Array.from({ length: width }, (_, c) => { const value = row[column + c] ?? ''; return ({ address: `${columnName(column + c + 1)}${headers && r === 0 ? 1 : start + r + (headers ? 0 : 1)}`, type: 'Text', value, formula: null, valueStatus: 'value', numberFormat: '@' }); }));
  }, [result, start, pageRows, headers, column, limits]);
  const projected = useMemo(() => spreadsheetSheet(name, cells, limits, undefined, headers), [name, cells, limits, headers]);
  return <Flex vertical gap={token.paddingSM} style={{ minWidth: 0 }}>
    <Flex className="file-preview-toolbar" wrap align="center" gap={token.paddingXS}>
      <Segmented aria-label="CSV view" options={['Grid', 'Text']} value={mode} onChange={setMode} />
      {mode === 'Grid' && <Checkbox checked={headers} disabled={limits.rowsPerWindow < 2} onChange={e => { setHeaders(e.target.checked); setPage(1); }}>First row is headers</Checkbox>}
      <Typography.Text type="secondary">{result ? `${bodyCount.toLocaleString()}${result.partial ? '+ preview' : ''} rows` : mode === 'Grid' ? 'Preparing grid…' : 'Original source'}</Typography.Text>
    </Flex>
    {mode === 'Text' ? <TextPreview policy={policy} text={source} name={name} markdown={false} /> : error ? <Alert type="warning" showIcon title={error} action={<Button onClick={() => setMode('Text')}>Read Text</Button>} /> : !result ? <Spin aria-label="Preparing grid" /> : <>
      <Typography.Text type="secondary">Read-only partial view · source rows {start + 1}–{Math.min(start + pageRows, result.rows.length)} · columns {columnName(column + 1)}–{columnName(Math.max(column + 1, Math.min(column + limits.columnsPerWindow, result.columns)))}{headers ? ' · first row repeated as headers' : ''}{result.partial ? ' · parsing limit reached; download for all fields' : ''}{projected.truncated ? ' · long values shortened; Text and Download retain originals' : ''}</Typography.Text>
      <Suspense fallback={<Spin aria-label="Loading spreadsheet" />}><ReadonlySpreadsheet key={`${page}:${columnPage}:${headers}`} sheet={projected.sheet} cells={cells} firstRow={headers ? 1 : start + 1} firstColumn={column + 1} /></Suspense>
      <Flex wrap gap={token.paddingXS}>
        {bodyCount > pageRows && <Pagination aria-label="CSV rows" size="small" simple current={page} pageSize={pageRows} total={bodyCount} onChange={setPage} showSizeChanger={false} />}
        {(result.columns > limits.columnsPerWindow) && <Flex align="center" gap={token.paddingXS}><Button disabled={columnPage === 1} onClick={() => setColumnPage(p => p - 1)}>Previous columns</Button><Button disabled={columnPage * limits.columnsPerWindow >= result.columns} onClick={() => setColumnPage(p => p + 1)}>Next columns</Button></Flex>}
      </Flex>
    </>}
  </Flex>;
}

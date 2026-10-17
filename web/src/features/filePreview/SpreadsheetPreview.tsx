import { lazy, Suspense, useEffect, useMemo, useState } from 'react';
import { Alert, Button, Empty, Flex, Input, Spin, Tabs, Typography, theme } from 'antd';
import { type FileRef, PreviewError, readFailure, fileReadLost } from './fileSource';
import { readOfficePreview, type OfficePreview } from './officeSource';
import type { PreviewOptions } from './previewOptions';
import { columnName, spreadsheetSheet } from './spreadsheetAdapter';
export function SpreadsheetPreview({ file, initial, policy, onReadLost }: { file: FileRef; initial: OfficePreview; policy: PreviewOptions; onReadLost?: () => void }) {
  const ReadonlySpreadsheet = useMemo(() => lazy(() => import('./ReadonlySpreadsheet').then(module => ({ default: module.ReadonlySpreadsheet }))), []);
  const { token } = theme.useToken();
  const [sheet, setSheet] = useState(initial.sheet!), [range, setRange] = useState(initial.range!);
  const [query, setQuery] = useState({ sheet: initial.sheet!, range: initial.range! });
  const [result, setResult] = useState<OfficePreview | null>(initial), [error, setError] = useState<string | null>(null);
  const identity = JSON.stringify(file);
  useEffect(() => {
    const controller = new AbortController(); setError(null);
    if (query.sheet === initial.sheet && query.range === initial.range) { setResult(initial); return; }
    setResult(null);
    void readOfficePreview(file, controller.signal, policy, query.sheet, query.range).then(value => {
      if (value.sha256 !== initial.sha256) throw readFailure(409);
      if (!controller.signal.aborted) setResult(value);
    }).catch(reason => { if (!controller.signal.aborted) { if (fileReadLost(reason)) onReadLost?.(); setError(reason instanceof PreviewError ? reason.message : 'The worksheet could not be loaded. Retry or reload this file.'); } });
    return () => controller.abort();
  }, [query, initial, identity, policy]);
  const cells = result?.rows ?? [];
  const view = result?.presentation;
  const projected = useMemo(() => spreadsheetSheet(sheet, result?.rows ?? [], policy.spreadsheet, result?.presentation), [sheet, result, policy]);
  const rowStep = Math.max(1, view?.rowDimensions.length ?? policy.spreadsheet.rowsPerWindow), columnStep = Math.max(1, view?.columnDimensions.length ?? policy.spreadsheet.columnsPerWindow);
  function navigate(rows: number, columns: number) {
    if (!view) return;
    const r = Math.max(1, view.firstRow + rows), c = Math.max(1, view.firstColumn + columns);
    const next = `${columnName(c)}${r}:${columnName(Math.min(16384, c + columnStep - 1))}${Math.min(1048576, r + rowStep - 1)}`;
    setRange(next); setQuery({ sheet, range: next });
  }
  return <Flex vertical gap={token.paddingSM} style={{ minWidth: 0 }}>
    <Typography.Text type="secondary">Partial workbook · {result?.range ?? query.range} · read-only, cached formula values.</Typography.Text>
    <Tabs className="file-preview-sheets" aria-label="Worksheets" activeKey={sheet} items={initial.sheets!.map(name => ({ key: name, label: name }))} onChange={name => { setSheet(name); setQuery({ sheet: name, range }); }} />
    <Flex className="file-preview-toolbar" wrap align="center" gap={token.paddingXS}>
      <Input aria-label="Worksheet range" className="file-preview-range" value={range} onChange={e => setRange(e.target.value)} onPressEnter={() => setQuery({ sheet, range })} />
      <Button onClick={() => setQuery({ sheet, range })}>Load range</Button>
      {view && view.firstRow > 1 && <Button onClick={() => navigate(-rowStep, 0)}>Previous rows</Button>}
      {view && view.firstRow + rowStep <= view.totalRows && <Button onClick={() => navigate(rowStep, 0)}>Next rows</Button>}
      {view && view.firstColumn > 1 && <Button onClick={() => navigate(0, -columnStep)}>Previous columns</Button>}
      {view && view.firstColumn + columnStep <= view.totalColumns && <Button onClick={() => navigate(0, columnStep)}>Next columns</Button>}
    </Flex>
    {view?.merges.some(merge => merge.clipped) && <Alert type="info" showIcon title="A merged range crosses this window. Its visible cells are shown separately; load the complete range to see the merge." />}
    {result && projected.truncated && <Alert type="info" showIcon title="Some cell text is shortened in this preview. Load a smaller range to reduce window limits, or download the original for complete values." />}
    {error ? <Alert type="warning" showIcon title={error} action={<Button onClick={() => setQuery({ sheet, range })}>Retry range</Button>} /> : !result ? <Spin aria-label="Loading worksheet" /> : !cells.length ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="This worksheet range is empty" /> : <Suspense fallback={<Spin aria-label="Loading spreadsheet" />}>
      <ReadonlySpreadsheet key={`${result.sheet}:${result.range}`} sheet={projected.sheet} cells={cells} firstRow={view?.firstRow} firstColumn={view?.firstColumn} />
    </Suspense>}
  </Flex>;
}

import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Flex, Input, Slider, Typography, theme } from 'antd';
import { Workbook, type WorkbookInstance } from '@fortune-sheet/react';
import '@fortune-sheet/react/dist/index.css';
import { columnName, type ReadonlySheet } from './spreadsheetAdapter';
import type { OfficeCell } from './officeSource';

export function ReadonlySpreadsheet({ sheet, cells, firstRow = 1, firstColumn = 1 }: { sheet: ReadonlySheet; cells: OfficeCell[][]; firstRow?: number; firstColumn?: number }) {
  const { token } = theme.useToken();
  const viewer = useRef<HTMLDivElement>(null);
  const workbook = useRef<WorkbookInstance>(null);
  const [selected, setSelected] = useState({ row: 0, column: 0 });
  const [address, setAddress] = useState(`${columnName(firstColumn)}${firstRow}`);
  const [zoom, setZoom] = useState(100);
  const [jump, setJump] = useState({ row: 0, column: 0, lastRow: 0, lastColumn: 0, revision: 0 });
  const data = useMemo(() => [{ ...sheet, zoomRatio: zoom / 100, luckysheet_select_save: [{ row: [jump.row, jump.lastRow], column: [jump.column, jump.lastColumn], row_focus: jump.row, column_focus: jump.column }] }], [sheet, zoom, jump]);
  const [copyState, setCopyState] = useState('Copy selected range');
  const [selectionError, setSelectionError] = useState(false);
  const cell = cells[selected.row]?.[selected.column];
  // Details share the canvas text budget. Text/Download retain the full source.
  const displayValue = String(sheet.data?.[selected.row]?.[selected.column]?.m ?? '');
  const currentAddress = cell?.address ?? `${columnName(firstColumn + selected.column)}${firstRow + selected.row}`;
  // The library's document paste handler parses HTML before its read-only check.
  // Stop paste in capture phase, before that handler can parse untrusted clipboard markup.
  useEffect(() => {
    const preventPaste = (event: ClipboardEvent) => {
      if (viewer.current?.contains(document.activeElement)) { event.preventDefault(); event.stopImmediatePropagation(); }
    };
    document.addEventListener('paste', preventPaste, true);
    return () => document.removeEventListener('paste', preventPaste, true);
  }, []);
  useLayoutEffect(() => {
    const node = viewer.current;
    if (!node) return;
    const contain = () => {
      node.querySelectorAll<HTMLElement>('.fortune-row-header, .fortune-col-header, .fortune-left-top, [contenteditable]').forEach(element => {
        element.tabIndex = -1;
        if (element.hasAttribute('contenteditable')) { element.contentEditable = 'false'; element.setAttribute('aria-readonly', 'true'); }
      });
    };
    contain();
    const observer = new MutationObserver(contain); observer.observe(node, { childList: true, subtree: true });
    return () => observer.disconnect();
  }, [data]);
  const hooks = useMemo(() => ({
    beforeUpdateCell: () => false, beforePaste: () => false,
    beforeAddSheet: () => false, beforeDeleteSheet: () => false, beforeUpdateSheetName: () => false,
    afterSelectionChange: (_id: string, selection: { row: number[]; column: number[] }) => {
      const row = selection.row[0], column = selection.column[0];
      setSelected({ row, column }); setAddress(cells[row]?.[column]?.address ?? `${columnName(firstColumn + column)}${firstRow + row}`);
    },
    beforeRenderRowHeaderCell: (_label: string, row: number, top: number, width: number, height: number, ctx: CanvasRenderingContext2D) => {
      ctx.save(); ctx.fillStyle = '#F4F4F4'; ctx.fillRect(0, top, width, height); ctx.fillStyle = '#333333'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle'; ctx.font = '12px Arial';
      ctx.fillText(cells[row]?.[0]?.address.replace(/^[A-Z]+/, '') ?? String(firstRow + row), width / 2, top + height / 2); ctx.restore(); return false;
    },
    beforeRenderColumnHeaderCell: (_label: string, column: number, left: number, width: number, height: number, ctx: CanvasRenderingContext2D) => {
      ctx.save(); ctx.fillStyle = '#F4F4F4'; ctx.fillRect(left, 0, width, height); ctx.fillStyle = '#333333'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle'; ctx.font = '12px Arial';
      ctx.fillText(columnName(firstColumn + column), left + width / 2, height / 2); ctx.restore(); return false;
    },
  }), [firstRow, firstColumn, cells]);
  useEffect(() => {
    const frame = requestAnimationFrame(() => workbook.current?.scroll({ targetRow: jump.row, targetColumn: jump.column }));
    return () => cancelAnimationFrame(frame);
  }, [jump.revision, zoom]);
  function selectedText() {
    const range = workbook.current?.getSelection()?.[0];
    if (!range) return '';
    const rows: string[] = [];
    for (let r = range.row[0]; r <= range.row[1]; r++) {
      const values: string[] = [];
      for (let c = range.column[0]; c <= range.column[1]; c++) values.push(String(workbook.current?.getCellValue(r, c, { type: 'v' }) ?? ''));
      rows.push(values.join('\t'));
    }
    return rows.join('\n');
  }
  return <Flex vertical gap={token.paddingSM} style={{ minWidth: 0 }}>
    <Flex className="file-preview-toolbar" wrap align="center" gap={token.paddingXS}>
      <Input aria-label="Selected cell" className="file-preview-range" value={address} onChange={e => setAddress(e.target.value)} onPressEnter={() => {
        const match = /^([A-Za-z]{1,3})([1-9][0-9]{0,6})$/.exec(address);
        let column = match ? [...match[1].toUpperCase()].reduce((value, ch) => value * 26 + ch.charCodeAt(0) - 64, 0) - firstColumn : -1;
        let row = match ? Number(match[2]) - firstRow : -1;
        const foundRow = cells.findIndex(cells => cells.some(cell => cell.address.toUpperCase() === address.toUpperCase()));
        if (foundRow >= 0) { row = foundRow; column = cells[row].findIndex(cell => cell.address.toUpperCase() === address.toUpperCase()); }
        if (row < 0 || column < 0 || row >= (sheet.row ?? 0) || column >= (sheet.column ?? 0)) { setSelectionError(true); return; }
        setSelectionError(false); setSelected({ row, column }); setJump(previous => ({ row, column, lastRow: row, lastColumn: column, revision: previous.revision + 1 }));
      }} />
      <Button onClick={() => void navigator.clipboard.writeText(selectedText()).then(() => setCopyState('Copied')).catch(() => setCopyState('Copy failed'))}>{copyState}</Button>
      <Typography.Text type="secondary">{zoom}%</Typography.Text>
      <Slider className="file-preview-sheet-zoom" aria-label="Spreadsheet zoom" ariaLabelForHandle="Spreadsheet zoom" min={50} max={150} step={10} value={zoom} onChange={value => {
        const selection = workbook.current?.getSelection()?.[0];
        if (selection) setJump(previous => ({ row: selection.row[0], column: selection.column[0], lastRow: selection.row[1], lastColumn: selection.column[1], revision: previous.revision + 1 }));
        setZoom(value);
      }} />
    </Flex>
    {selectionError && <Alert type="info" title="Choose a cell within the displayed range." />}
    <Typography.Paragraph className="file-preview-cell-detail" style={{ margin: 0 }} aria-live="polite">
      {currentAddress} · {displayValue || (cell?.value ? 'Value omitted by the preview text budget' : 'Empty cell')}{cell?.formula ? ` · =${cell.formula} · ${cell.valueStatus.replaceAll('_', ' ')}` : ''}
    </Typography.Paragraph>
    <div ref={viewer} className="file-preview-spreadsheet" role="region" aria-label="Read-only spreadsheet" tabIndex={0}
      onKeyDownCapture={event => {
        if (event.key === 'Tab') { event.stopPropagation(); return; }
        const command = event.ctrlKey || event.metaKey;
        if (command && ['c', 'a'].includes(event.key.toLowerCase()) || ['Tab', 'Escape', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'PageUp', 'PageDown'].includes(event.key)) return;
        event.preventDefault(); event.stopPropagation();
      }}
      onCopyCapture={event => { event.preventDefault(); event.stopPropagation(); event.clipboardData.setData('text/plain', selectedText()); }}
      onCutCapture={event => { event.preventDefault(); event.stopPropagation(); }}
      onDropCapture={event => { event.preventDefault(); event.stopPropagation(); }}
      onDoubleClickCapture={event => { event.preventDefault(); event.stopPropagation(); }}>
      <Workbook key={`${zoom}:${jump.revision}`} ref={workbook} data={data} lang="en" allowEdit={false} showToolbar={false} showFormulaBar={false} showSheetTabs={false} forceCalculation={false}
        row={sheet.row} column={sheet.column} addRows={0} cellContextMenu={['copy']} headerContextMenu={[]} sheetTabContextMenu={[]} filterContextMenu={[]} hooks={hooks} />
    </div>
  </Flex>;
}

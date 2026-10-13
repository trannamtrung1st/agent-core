import { useEffect, useState } from 'react';
import {
  Alert,
  Button,
  Empty,
  Flex,
  Input,
  Spin,
  Table,
  Tabs,
  Typography,
  theme,
} from 'antd';
import { type FileRef, PreviewError, readFailure } from './fileSource';
import {
  readOfficePreview,
  type OfficeCell,
  type OfficePreview,
} from './officeSource';
export function SpreadsheetPreview({
  file,
  initial,
}: {
  file: FileRef;
  initial: OfficePreview;
}) {
  const { token } = theme.useToken();
  const [sheet, setSheet] = useState(initial.sheet!);
  const [range, setRange] = useState(initial.range!);
  const [query, setQuery] = useState({
    sheet: initial.sheet!,
    range: initial.range!,
  });
  const [result, setResult] = useState<OfficePreview | null>(initial);
  const [selected, setSelected] = useState<OfficeCell | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    setSelected(null);
    setError(null);
    if (query.sheet === initial.sheet && query.range === initial.range) {
      setResult(initial);
      return;
    }
    setResult(null);
    void readOfficePreview(file, controller.signal, query.sheet, query.range)
      .then((value) => {
        if (value.sha256 !== initial.sha256) throw readFailure(409);
        if (!controller.signal.aborted) setResult(value);
      })
      .catch((reason) => {
        if (!controller.signal.aborted)
          setError(
            reason instanceof PreviewError
              ? reason.message
              : 'The worksheet could not be loaded. Retry or reload this file.',
          );
      });
    return () => controller.abort();
  }, [query, initial, file]);
  const rows = result?.rows ?? [];
  const count = rows[0]?.length ?? 0;
  return (
    <Flex vertical gap={token.paddingSM} style={{ minWidth: 0 }}>
      <Typography.Text type="secondary">
        Read-only range · formulas are not recalculated. Additional Office
        features may be present in the download.
      </Typography.Text>
      <Tabs
        aria-label="Worksheets"
        activeKey={sheet}
        items={initial.sheets!.map((name) => ({ key: name, label: name }))}
        onChange={(name) => {
          setSheet(name);
          setQuery({ sheet: name, range });
        }}
      />
      <Flex wrap align="center" gap={token.paddingXS}>
        <Input
          aria-label="Worksheet range"
          value={range}
          onChange={(event) => setRange(event.target.value)}
          onPressEnter={() => setQuery({ sheet, range })}
          style={{ width: 180, maxWidth: '100%' }}
        />
        <Button onClick={() => setQuery({ sheet, range })}>Load range</Button>
      </Flex>
      {selected && (
        <Typography.Paragraph style={{ margin: 0 }} aria-live="polite">
          {selected.address}
          {selected.formula
            ? ` · =${selected.formula} · ${selected.valueStatus.replaceAll('_', ' ')}`
            : ` · ${selected.value} · ${selected.type}`}
        </Typography.Paragraph>
      )}
      {error ? (
        <Alert
          type="warning"
          showIcon
          title={error}
          action={
            <Button onClick={() => setQuery({ sheet, range })}>
              Retry range
            </Button>
          }
        />
      ) : !result ? (
        <Spin aria-label="Loading worksheet" />
      ) : !rows.length ? (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="This worksheet range is empty"
        />
      ) : (
        <Table<{ key: string; cells: OfficeCell[] }>
          className="file-preview-grid"
          size="small"
          pagination={{
            pageSize: 50,
            hideOnSinglePage: true,
            showSizeChanger: false,
          }}
          rowKey="key"
          bordered
          dataSource={rows.map((cells, i) => ({
            key: cells[0]?.address ?? String(i),
            cells,
          }))}
          scroll={{ x: Math.max(600, count * 140), y: 400 }}
          columns={Array.from({ length: count }, (_, index) => ({
            title: rows[0][index].address.replace(/[0-9]+$/, ''),
            width: 140,
            render: (_: unknown, row: { cells: OfficeCell[] }) => {
              const cell = row.cells[index];
              return (
                <Button
                  type="text"
                  className="file-preview-cell"
                  aria-label={`${cell.address} ${cell.value || 'empty'}${cell.formula ? ' formula' : ''}`}
                  title={cell.address}
                  onClick={() => setSelected(cell)}
                >
                  {cell.value ||
                    (cell.formula ? 'Cached value unavailable' : '')}
                </Button>
              );
            },
          }))}
        />
      )}
    </Flex>
  );
}

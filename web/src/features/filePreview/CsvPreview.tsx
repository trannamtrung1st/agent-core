import { useEffect, useState } from 'react';
import {
  Alert,
  Button,
  Checkbox,
  Flex,
  Pagination,
  Segmented,
  Table,
  Typography,
  theme,
} from 'antd';
import { extension, previewLimits } from './previewPolicy';
import { TextPreview } from './TextPreview';
import type { parseDelimited } from './parseDelimited';
import { processPreview } from './previewWorker';
export function CsvPreview({
  source,
  name,
  tabSeparated = false,
}: {
  source: string;
  name: string;
  tabSeparated?: boolean;
}) {
  const { token } = theme.useToken();
  const [mode, setMode] = useState('Grid');
  const [headers, setHeaders] = useState(true);
  const [page, setPage] = useState(1);
  const [result, setResult] = useState<ReturnType<
    typeof parseDelimited
  > | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    void processPreview<ReturnType<typeof parseDelimited>>(
      'csv',
      source,
      tabSeparated || extension(name) === 'tsv' ? '\t' : ',',
      controller.signal,
    )
      .then((value) => {
        if (!controller.signal.aborted) setResult(value);
      })
      .catch((e) => {
        if (!controller.signal.aborted) setError(e.message);
      });
    return () => controller.abort();
  }, [source, name, tabSeparated]);
  const rows = result?.rows ?? [];
  const body = headers ? rows.slice(1) : rows;
  const count = rows.reduce((max, row) => Math.max(max, row.length), 0);
  return (
    <Flex vertical gap={token.paddingSM} style={{ minWidth: 0 }}>
      <Flex wrap align="center" gap={token.paddingXS}>
        <Segmented
          aria-label="CSV view"
          options={['Grid', 'Text']}
          value={mode}
          onChange={(value) => setMode(value)}
        />
        {mode === 'Grid' && (
          <Checkbox
            checked={headers}
            onChange={(event) => {
              setHeaders(event.target.checked);
              setPage(1);
            }}
          >
            First row is headers
          </Checkbox>
        )}
        <Typography.Text type="secondary">
          {result
            ? `${body.length.toLocaleString()}${result.partial ? '+ preview' : ''} rows`
            : 'Preparing grid…'}
        </Typography.Text>
      </Flex>
      {mode === 'Text' ? (
        <TextPreview text={source} name={name} markdown={false} />
      ) : error ? (
        <Alert
          type="warning"
          showIcon
          title={error}
          action={<Button onClick={() => setMode('Text')}>Read Text</Button>}
        />
      ) : (
        <>
          {result?.partial && (
            <Typography.Text type="secondary">
              Partial preview · first 50,000 rows. Download for all rows.
            </Typography.Text>
          )}
          <Table<{ key: number; cells: string[] }>
            size="small"
            className="file-preview-grid"
            bordered
            virtual
            pagination={false}
            loading={!result}
            rowKey="key"
            dataSource={body
              .slice(
                (page - 1) * previewLimits.pageRows,
                page * previewLimits.pageRows,
              )
              .map((cells, i) => ({
                key: (page - 1) * previewLimits.pageRows + i,
                cells,
              }))}
            scroll={{ x: Math.max(600, count * 140), y: 400 }}
            columns={Array.from({ length: count }, (_, i) => ({
              title: headers
                ? rows[0]?.[i] || `Column ${i + 1}`
                : `Column ${i + 1}`,
              width: 140,
              render: (_: unknown, row: { cells: string[] }) => (
                <Typography.Text copyable={{ text: row.cells[i] ?? '' }}>
                  {row.cells[i] ?? ''}
                </Typography.Text>
              ),
            }))}
          />
          <Pagination
            size="small"
            simple
            current={page}
            pageSize={previewLimits.pageRows}
            total={body.length}
            onChange={setPage}
            showSizeChanger={false}
          />
        </>
      )}
    </Flex>
  );
}

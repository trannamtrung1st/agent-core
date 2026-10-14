import type { PreviewOptions } from './previewOptions';
import { useState } from 'react';
import {
  Alert,
  Button,
  Checkbox,
  Empty,
  Flex,
  Input,
  Segmented,
  Typography,
  theme,
} from 'antd';
import { MarkdownMessage } from '../chat/MarkdownMessage';
import { HighlightedCode } from './HighlightedCode';
import { extension } from './previewPolicy';
export function TextPreview({
  policy,
  text,
  name,
  markdown,
}: {
  policy: PreviewOptions;
  text: string;
  name: string;
  markdown: boolean;
}) {
  const { token } = theme.useToken();
  const [mode, setMode] = useState(markdown ? 'Rendered' : 'Source');
  const [wrap, setWrap] = useState(false);
  const [query, setQuery] = useState('');
  const [copyStatus, setCopyStatus] = useState('Copy');
  const exceedsLines = text.split('\n', policy.text.maxLines + 1).length > policy.text.maxLines;
  const hits = query
    ? text.toLocaleLowerCase().split(query.toLocaleLowerCase()).length - 1
    : 0;
  return (
    <Flex vertical gap={token.paddingSM} style={{ minWidth: 0 }}>
      <Flex
        className="file-preview-toolbar"
        wrap
        gap={token.paddingXS}
        align="center"
      >
        {markdown && (
          <Segmented
            aria-label="Markdown view"
            options={['Rendered', 'Source']}
            value={mode}
            onChange={setMode}
          />
        )}
        {mode === 'Source' && (
          <Checkbox
            checked={wrap}
            onChange={(event) => setWrap(event.target.checked)}
          >
            Wrap lines
          </Checkbox>
        )}
        <Button
          onClick={() => {
            void navigator.clipboard
              .writeText(text)
              .then(() => setCopyStatus('Copied'))
              .catch(() => setCopyStatus('Copy failed'));
          }}
        >
          {copyStatus}
        </Button>
        <Input
          allowClear
          type="search"
          aria-label="Find in file"
          placeholder="Find in file"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          style={{ width: 200, maxWidth: '100%' }}
        />
        {query && (
          <Typography.Text role="status">
            {hits} matches in source
          </Typography.Text>
        )}
      </Flex>
      {exceedsLines ? <Alert type="info" showIcon title="The source exceeds the configured text line limit. Copy or download the original for all lines." /> : !text ? (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="Empty document"
        />
      ) : mode === 'Rendered' ? (
        <MarkdownMessage source={text} />
      ) : (
        <HighlightedCode
          policy={policy}
          source={text}
          language={extension(name)}
          wrap={wrap}
          query={query}
        />
      )}
    </Flex>
  );
}

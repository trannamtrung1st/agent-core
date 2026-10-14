import { loadPreviewOptions, type PreviewOptions } from './previewOptions';
import { useEffect, useState } from 'react';
import type { ThemedToken } from 'shiki/core';
import { processPreview } from './previewWorker';

export function HighlightedCode({
  policy,
  source,
  language = 'text',
  wrap = false,
  query = '',
}: {
  policy?: PreviewOptions;
  source: string;
  language?: string;
  wrap?: boolean;
  query?: string;
}) {
  const [effective, setEffective] = useState(policy);
  useEffect(() => { if (policy) setEffective(policy); else void loadPreviewOptions().then(setEffective).catch(() => undefined); }, [policy]);
  const [result, setResult] = useState<{
    source: string;
    language: string;
    tokens: ThemedToken[][];
  } | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    if (
      effective && new TextEncoder().encode(source).byteLength <= effective.text.highlightMaxBytes &&
      source.split('\n').length <= effective.text.highlightMaxLines
    )
      void processPreview<ThemedToken[][]>(
        'code',
        source,
        language,
        controller.signal,
        effective,
      )
        .then((tokens) => {
          if (!controller.signal.aborted)
            setResult({ source, language, tokens });
        })
        .catch(() => undefined);
    return () => controller.abort();
  }, [source, language, effective]);
  const tokens =
    result?.source === source && result.language === language
      ? result.tokens
      : null;
  // React escapes every token. Never inject Shiki HTML or document-supplied styles.
  const plain = source.split('\n');
  function matched(line: string) {
    if (!query) return line;
    const parts = [];
    let from = 0;
    const lower = line.toLocaleLowerCase();
    const needle = query.toLocaleLowerCase();
    let hit = lower.indexOf(needle);
    while (hit >= 0) {
      parts.push(
        line.slice(from, hit),
        <mark key={hit}>{line.slice(hit, hit + query.length)}</mark>,
      );
      from = hit + query.length;
      hit = lower.indexOf(needle, from);
    }
    parts.push(line.slice(from));
    return parts;
  }
  return (
    <pre className={`file-preview-code${wrap ? ' file-preview-wrap' : ''}`}>
      <code>
        {plain.map((line, i) => (
          <span className="file-preview-line" key={i}>
            <span aria-hidden className="file-preview-line-number">
              {i + 1}
            </span>
            {tokens && !query
              ? tokens[i]?.map((token, j) => (
                  <span style={{ color: token.color }} key={j}>
                    {token.content}
                  </span>
                ))
              : matched(line)}
            {i < plain.length - 1 ? '\n' : ''}
          </span>
        ))}
      </code>
    </pre>
  );
}

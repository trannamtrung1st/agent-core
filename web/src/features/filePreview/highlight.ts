import { createHighlighterCore } from 'shiki/core';
import { createJavaScriptRegexEngine } from 'shiki/engine/javascript';
import githubDark from 'shiki/themes/github-dark.mjs';
import javascript from 'shiki/langs/javascript.mjs';
import typescript from 'shiki/langs/typescript.mjs';
import jsx from 'shiki/langs/jsx.mjs';
import tsx from 'shiki/langs/tsx.mjs';
import json from 'shiki/langs/json.mjs';
import yaml from 'shiki/langs/yaml.mjs';
import toml from 'shiki/langs/toml.mjs';
import xml from 'shiki/langs/xml.mjs';
import csharp from 'shiki/langs/csharp.mjs';
import python from 'shiki/langs/python.mjs';
import sql from 'shiki/langs/sql.mjs';
import css from 'shiki/langs/css.mjs';
const highlighter = createHighlighterCore({
  themes: [githubDark],
  langs: [
    javascript,
    typescript,
    jsx,
    tsx,
    json,
    yaml,
    toml,
    xml,
    csharp,
    python,
    sql,
    css,
  ],
  engine: createJavaScriptRegexEngine(),
});
const aliases: Record<string, string> = {
  js: 'javascript',
  ts: 'typescript',
  cs: 'csharp',
  py: 'python',
  yml: 'yaml',
  jsonl: 'json',
  ndjson: 'json',
};
export async function highlight(source: string, language: string) {
  const instance = await highlighter;
  const lang = aliases[language] ?? language;
  return instance.codeToTokens(source, {
    lang: instance.getLoadedLanguages().includes(lang) ? lang : 'text',
    theme: 'github-dark',
  }).tokens;
}

import { useState, type ReactNode } from "react";
import { Button } from "antd";
import { CopyOutlined } from "@ant-design/icons";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import rehypeSanitize from "rehype-sanitize";
import { authorizedHref } from "./sanitizedMarkdown";

export function MarkdownMessage({ source }: { source: string }) {
  return (
    <div className="markdown-message">
      <Markdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[rehypeSanitize]}
        skipHtml
        urlTransform={(url) => authorizedHref(url) ?? ""}
        components={{
          img: ({ alt }) => (
            <span className="md-image-placeholder">{alt ? `[Image: ${alt}]` : "[Image]"}</span>
          ),
          a: ({ href, children }) => {
            const safe = href ? authorizedHref(href) : null;
            if (!safe) {
              return <span>{children}</span>;
            }

            return (
              <a href={safe} rel="noreferrer noopener" target="_blank">
                {children}
              </a>
            );
          },
          pre: ({ children }) => <CodeBlock>{children}</CodeBlock>
        }}
      >
        {source}
      </Markdown>
    </div>
  );
}

function CodeBlock({ children }: { children?: ReactNode }) {
  const [copied, setCopied] = useState(false);
  const text = codeText(children);

  if (!text.trim()) {
    return null;
  }

  async function copy() {
    if (!text) {
      return;
    }

    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      setCopied(false);
    }
  }

  return (
    <div className="md-code-block">
      <Button
        type="text"
        size="small"
        className="md-code-copy"
        aria-label={copied ? "Copied" : "Copy code"}
        icon={<CopyOutlined />}
        onClick={() => void copy()}
      >
        {copied ? "Copied" : "Copy"}
      </Button>
      <pre>{children}</pre>
    </div>
  );
}

function codeText(node: ReactNode): string {
  if (typeof node === "string" || typeof node === "number") {
    return String(node);
  }

  if (!node || typeof node !== "object") {
    return "";
  }

  if (Array.isArray(node)) {
    return node.map(codeText).join("");
  }

  if ("props" in node) {
    const props = (node as { props?: { children?: ReactNode } }).props;
    return codeText(props?.children);
  }

  return "";
}

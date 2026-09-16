export function authorizedHref(raw: string): string | null {
  const trimmed = raw.trim();
  if (!trimmed) {
    return null;
  }

  try {
    const url = new URL(trimmed);
    if (url.protocol !== "https:" && url.protocol !== "http:") {
      return null;
    }

    if (url.username || url.password) {
      return null;
    }

    return url.href;
  } catch {
    return null;
  }
}

export type MarkdownNode =
  | { type: "text"; value: string }
  | { type: "strong"; value: string }
  | { type: "em"; value: string }
  | { type: "code"; value: string }
  | { type: "link"; href: string; value: string };

export function parseSanitizedMarkdown(source: string): MarkdownNode[] {
  const nodes: MarkdownNode[] = [];
  const text = source ?? "";
  let index = 0;

  const pushText = (value: string) => {
    if (value.length === 0) {
      return;
    }

    const last = nodes.at(-1);
    if (last?.type === "text") {
      last.value += value;
      return;
    }

    nodes.push({ type: "text", value });
  };

  while (index < text.length) {
    if (text.startsWith("**", index)) {
      const end = text.indexOf("**", index + 2);
      if (end > index + 2) {
        nodes.push({ type: "strong", value: text.slice(index + 2, end) });
        index = end + 2;
        continue;
      }
    }

    if (text[index] === "*" && text[index + 1] !== "*") {
      const end = text.indexOf("*", index + 1);
      if (end > index + 1) {
        nodes.push({ type: "em", value: text.slice(index + 1, end) });
        index = end + 1;
        continue;
      }
    }

    if (text[index] === "`") {
      const end = text.indexOf("`", index + 1);
      if (end > index + 1) {
        nodes.push({ type: "code", value: text.slice(index + 1, end) });
        index = end + 1;
        continue;
      }
    }

    if (text[index] === "[") {
      const labelEnd = text.indexOf("](", index + 1);
      const hrefEnd = labelEnd >= 0 ? text.indexOf(")", labelEnd + 2) : -1;
      if (labelEnd > index + 1 && hrefEnd > labelEnd + 2) {
        const label = text.slice(index + 1, labelEnd);
        const href = authorizedHref(text.slice(labelEnd + 2, hrefEnd));
        if (href) {
          nodes.push({ type: "link", href, value: label });
          index = hrefEnd + 1;
          continue;
        }
      }
    }

    pushText(text[index]!);
    index += 1;
  }

  return nodes;
}

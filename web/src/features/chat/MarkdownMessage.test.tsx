import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { MarkdownMessage } from "./MarkdownMessage";

describe("MarkdownMessage", () => {
  it("renders headings lists code tables and links", () => {
    render(
      <MarkdownMessage
        source={`## Title

A **bold** and *em* plus \`code\` and [docs](https://example.com/a)

- one
  - nested
- two

1. first
2. second

> quoted

---

| Col | Val |
| --- | --- |
| A | 1 |

\`\`\`
line
\`\`\`
`}
      />
    );

    expect(screen.getByRole("heading", { name: "Title" })).toBeInTheDocument();
    expect(screen.getByText("bold").tagName).toBe("STRONG");
    expect(screen.getByText("em").tagName).toBe("EM");
    expect(screen.getByText("code").tagName).toBe("CODE");
    expect(screen.getByRole("link", { name: "docs" })).toHaveAttribute("href", "https://example.com/a");
    expect(screen.getByText("one")).toBeInTheDocument();
    expect(screen.getByText("nested")).toBeInTheDocument();
    expect(screen.getByText("first").closest("ol")).not.toBeNull();
    expect(screen.getByText("quoted").closest("blockquote")).not.toBeNull();
    expect(document.querySelector(".markdown-message hr")).not.toBeNull();
    expect(screen.getByRole("columnheader", { name: "Col" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy code" })).toBeInTheDocument();
  });

  it("does not execute raw HTML or javascript links", () => {
    render(
      <MarkdownMessage source={`Hello <script data-testid="injected">alert(1)</script> <img alt="x" src="x" onerror="alert(1)" /> [x](javascript:alert(1))`} />
    );

    expect(document.querySelector("script")).toBeNull();
    expect(document.querySelector("img")).toBeNull();
    expect(screen.queryByRole("link", { name: "x" })).not.toBeInTheDocument();
    expect(screen.getByText(/Hello/)).toBeInTheDocument();
  });
});

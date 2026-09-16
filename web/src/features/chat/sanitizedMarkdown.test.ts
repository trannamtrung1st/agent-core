import { describe, expect, it } from "vitest";
import { authorizedHref, parseSanitizedMarkdown } from "./sanitizedMarkdown";

describe("sanitizedMarkdown", () => {
  it("keeps bold italic code and https links", () => {
    const nodes = parseSanitizedMarkdown("See **bold** and *em* plus `code` and [docs](https://example.com/a)");
    expect(nodes).toEqual([
      { type: "text", value: "See " },
      { type: "strong", value: "bold" },
      { type: "text", value: " and " },
      { type: "em", value: "em" },
      { type: "text", value: " plus " },
      { type: "code", value: "code" },
      { type: "text", value: " and " },
      { type: "link", href: "https://example.com/a", value: "docs" }
    ]);
  });

  it("rejects javascript and credentialed urls", () => {
    expect(authorizedHref("javascript:alert(1)")).toBeNull();
    expect(authorizedHref("https://user:pass@example.com")).toBeNull();
    const nodes = parseSanitizedMarkdown("[x](javascript:alert(1))");
    expect(nodes.every((node) => node.type !== "link")).toBe(true);
  });
});

import { theme } from "antd";
import { describe, expect, it } from "vitest";
import { antdTheme } from "./antdTheme";

describe("antdTheme", () => {
  it("uses Ant Design dark algorithm with the product primary", () => {
    expect(antdTheme.algorithm).toBe(theme.darkAlgorithm);
    expect(antdTheme.token?.colorPrimary).toBe("#1677ff");
  });
});

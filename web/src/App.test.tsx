import { act, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { App } from "./App";

vi.mock("./services/realtime", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./services/realtime")>();
  return {
    ...actual,
    bootstrap: vi.fn().mockResolvedValue(undefined)
  };
});

describe("App", () => {
  it("renders the synthetic chat shell", async () => {
    await act(async () => {
      render(<App />);
    });
    expect(screen.getByRole("heading", { name: "Agent Core" })).toBeInTheDocument();
    expect(screen.getByTestId("profile")).toHaveTextContent("Synthetic");
  });
});

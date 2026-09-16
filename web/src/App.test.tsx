import { act, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { emptySession, useSessionStore } from "./state/sessionStore";

vi.mock("./services/realtime", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./services/realtime")>();
  return {
    ...actual,
    bootstrap: vi.fn().mockResolvedValue("Synthetic")
  };
});

describe("App", () => {
  afterEach(() => {
    act(() => {
      useSessionStore.setState({
        ...emptySession(),
        agents: [],
        selectedAgentId: "examiner"
      });
    });
  });

  it("renders the synthetic chat shell", async () => {
    await act(async () => {
      render(<App />);
    });
    expect(screen.getByRole("heading", { name: "Agent Core" })).toBeInTheDocument();
    expect(screen.getByTestId("profile")).toHaveTextContent("Synthetic");
  });
});

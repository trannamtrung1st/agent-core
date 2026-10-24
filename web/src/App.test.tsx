import { render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { bootstrap } from "./services/realtime";

vi.mock("./services/realtime", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./services/realtime")>();
  return {
    ...actual,
    bootstrap: vi.fn().mockResolvedValue("Synthetic")
  };
});

describe("App", () => {
  beforeEach(() => {
    window.history.replaceState(null, "", "/");
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ reasonCode: "authentication_required" }), {
      status: 401,
      headers: { "Content-Type": "application/json" }
    })));
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("requires sign-in at the account entry before mounting private Chat", async () => {
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeVisible();
    expect(window.location.pathname).toBe("/sign-in");
    expect(new URLSearchParams(window.location.search).get("returnTo")).toBe("/account/organizations");
    expect(screen.queryByTestId("profile")).not.toBeInTheDocument();
    expect(bootstrap).not.toHaveBeenCalled();
  });
});

import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { App } from "./App";

describe("App", () => {
  it("renders the synthetic skeleton", () => {
    render(<App />);
    expect(screen.getByRole("heading", { name: "Agent Core" })).toBeInTheDocument();
    expect(screen.getByTestId("profile")).toHaveTextContent("Synthetic");
  });
});

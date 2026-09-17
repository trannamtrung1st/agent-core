import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { IdentityPicker } from "./IdentityPicker";

const agents = [
  {
    id: "customer-support",
    version: 1,
    name: "Sam",
    role: "Support",
    description: "Help.",
    voiceAvailable: false
  },
  {
    id: "compliance",
    version: 1,
    name: "Jordan",
    role: "Compliance",
    description: "Cite.",
    voiceAvailable: false
  }
];

function openIdentityOptions() {
  fireEvent.mouseDown(screen.getByRole("combobox", { name: "Identity" }));
}

describe("IdentityPicker", () => {
  it("lists Support and Compliance identities for a new durable chat", () => {
    const onSelect = vi.fn();
    const onStart = vi.fn();
    render(
      <IdentityPicker
        agents={agents}
        selectedAgentId="customer-support"
        error={null}
        onSelect={onSelect}
        onStart={onStart}
      />
    );

    expect(screen.getByRole("combobox", { name: "Identity" })).toBeInTheDocument();
    expect(screen.getByText("Sam — Support")).toBeInTheDocument();

    openIdentityOptions();
    fireEvent.click(screen.getByTitle("Jordan — Compliance"));
    expect(onSelect).toHaveBeenCalledWith("compliance");

    fireEvent.click(screen.getByRole("button", { name: "Start conversation" }));
    expect(onStart).toHaveBeenCalled();
  });

  it("surfaces picker errors without changing the start callback", () => {
    const onStart = vi.fn();
    render(
      <IdentityPicker
        agents={agents}
        selectedAgentId="customer-support"
        error="Unable to list agents."
        onSelect={vi.fn()}
        onStart={onStart}
      />
    );

    expect(screen.getByRole("alert")).toHaveTextContent("Unable to list agents.");
    fireEvent.click(screen.getByRole("button", { name: "Start conversation" }));
    expect(onStart).toHaveBeenCalled();
  });

  it("disables identity selection and start when no agents are available", () => {
    render(
      <IdentityPicker
        agents={[]}
        selectedAgentId=""
        error={null}
        onSelect={vi.fn()}
        onStart={vi.fn()}
      />
    );

    expect(screen.getByRole("combobox", { name: "Identity" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Start conversation" })).toBeDisabled();
  });
});

import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { IdentityPicker } from "./IdentityPicker";

describe("IdentityPicker", () => {
  it("lists Support and Compliance identities for a new durable chat", () => {
    const onSelect = vi.fn();
    const onStart = vi.fn();
    render(
      <IdentityPicker
        agents={[
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
        ]}
        selectedAgentId="customer-support"
        error={null}
        onSelect={onSelect}
        onStart={onStart}
      />
    );
    const identity = screen.getByLabelText("Identity");
    expect(identity).toHaveTextContent("Sam — Support");
    fireEvent.click(identity);
    fireEvent.pointerDown(screen.getByRole("option", { name: /Jordan/ }));
    expect(onSelect).toHaveBeenCalledWith("compliance");
    fireEvent.click(screen.getByRole("button", { name: "Start conversation" }));
    expect(onStart).toHaveBeenCalled();
  });
});

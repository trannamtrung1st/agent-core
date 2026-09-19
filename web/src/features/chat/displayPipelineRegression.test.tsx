import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import displayPipeline from "../../../../tests/fixtures/display-pipeline.json";
import { MarkdownMessage } from "./MarkdownMessage";

type OrdinaryCase = {
  id: string;
  rawFinalModelText: string;
  expectedDisplayText: string;
};

type ModelQualityCase = OrdinaryCase & { expectCodeBlock: boolean };

type Fixture = {
  ordinaryProse: OrdinaryCase[];
  modelFormattingQuality: ModelQualityCase[];
};

const fixture = displayPipeline as Fixture;

describe("display pipeline regression", () => {
  it("ordinary prose reaches MarkdownMessage source unchanged", () => {
    for (const caseDef of fixture.ordinaryProse) {
      const { container } = render(<MarkdownMessage source={caseDef.expectedDisplayText} key={caseDef.id} />);
      const message = container.querySelector(".markdown-message");
      expect(message).not.toBeNull();
      expect(message!.querySelectorAll(":scope > .md-code-block")).toHaveLength(0);
      if (caseDef.id === "plain-paragraphs") {
        expect(screen.getByText("ordinary display prose", { exact: false })).toBeInTheDocument();
      } else if (caseDef.id === "heading-and-list") {
        expect(screen.getByText("Closing sentence.")).toBeInTheDocument();
      } else {
        expect(screen.getByText("Visible display", { exact: false })).toBeInTheDocument();
      }
    }
  });

  it("model formatting quality cases render code blocks from raw markdown, not Agent Core transforms", () => {
    for (const caseDef of fixture.modelFormattingQuality) {
      const { container } = render(<MarkdownMessage source={caseDef.expectedDisplayText} />);
      expect(caseDef.expectedDisplayText).toBe(caseDef.rawFinalModelText);
      const message = container.querySelector(".markdown-message");
      expect(message).not.toBeNull();
      if (caseDef.expectCodeBlock) {
        expect(message!.querySelectorAll(":scope > .md-code-block").length).toBeGreaterThan(0);
      }
    }
  });
});

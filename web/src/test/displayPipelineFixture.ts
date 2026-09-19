import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

export type DisplayPipelineOrdinaryCase = {
  id: string;
  rawFinalModelText: string;
  expectedDisplayText: string;
};

export type DisplayPipelineModelQualityCase = DisplayPipelineOrdinaryCase & { expectCodeBlock: boolean };

export type DisplayPipelineFixture = {
  ordinaryProse: DisplayPipelineOrdinaryCase[];
  modelFormattingQuality: DisplayPipelineModelQualityCase[];
};

const fixturePath = join(
  dirname(fileURLToPath(import.meta.url)),
  "../../../tests/fixtures/display-pipeline.json"
);

export function loadDisplayPipelineFixture(): DisplayPipelineFixture {
  return JSON.parse(readFileSync(fixturePath, "utf8")) as DisplayPipelineFixture;
}

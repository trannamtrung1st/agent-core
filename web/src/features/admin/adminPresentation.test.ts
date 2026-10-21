import { describe, expect, it } from 'vitest';
import { defaultForkSourceVersion, formatForkSourceOptionLabel, formatDefinitionVersionSummary } from './adminPresentation';

describe("defaultForkSourceVersion", () => {
  it("prefers the highest non-deprecated version", () => {
    expect(
      defaultForkSourceVersion([
        {
          definitionId: "examiner",
          version: 3,
          source: "durable",
          status: "deprecated",
          displayName: "v3"
        },
        {
          definitionId: "examiner",
          version: 2,
          source: "durable",
          status: "published",
          displayName: "v2"
        },
        {
          definitionId: "examiner",
          version: 1,
          source: "builtIn",
          status: "published",
          displayName: "v1"
        }
      ])
    ).toBe(2);
  });

  it("falls back to the highest version when every version is deprecated", () => {
    expect(
      defaultForkSourceVersion([
        {
          definitionId: "examiner",
          version: 2,
          source: "durable",
          status: "deprecated",
          displayName: "v2"
        },
        {
          definitionId: "examiner",
          version: 1,
          source: "builtIn",
          status: "deprecated",
          displayName: "v1"
        }
      ])
    ).toBe(2);
  });
});
describe("formatForkSourceOptionLabel", () => {
  it("includes source and status in the fork dropdown", () => {
    expect(
      formatForkSourceOptionLabel({
        definitionId: "examiner",
        version: 3,
        source: "durable",
        status: "deprecated",
        displayName: "v3"
      })
    ).toBe("v3 · Durable · Deprecated");
  });

  it("labels deprecated latest separately from latest active", () => {
    const versions = [
      {
        definitionId: "examiner",
        version: 6,
        source: "durable",
        status: "deprecated",
        displayName: "Examiner v6"
      },
      {
        definitionId: "examiner",
        version: 5,
        source: "durable",
        status: "published",
        displayName: "Examiner v5"
      }
    ];
    expect(defaultForkSourceVersion(versions)).toBe(5);
    expect(formatDefinitionVersionSummary({
      latestVersion: 6,
      latestStatus: "deprecated",
      versions
    })).toBe("Latest v6 · Deprecated · Latest active v5");
  });
});
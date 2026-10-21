import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { App } from "antd";
import { applyCandidateJson, candidateForPersistence, candidateToJson, type DefinitionCandidate } from "./definitionCandidate";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { DefinitionCandidateEditor } from "./definitionCandidateEditor";
import { DefinitionSkillsSection } from "./DefinitionSkillsSection";

import { exposedRoles } from "../../test/exposedRoles";

vi.mock("../../services/adminApi", () => ({
  getExecutionBudgetLimits: vi.fn().mockResolvedValue({ maxSteps: 144, durationSeconds: 900, perToolSeconds: 30 }),
  listAdminAuthoringOptions: vi.fn()
}));
import { listAdminAuthoringOptions } from '../../services/adminApi';

const authoringOptions = {
  languageModelAliases: ["backup-llm", "primary-llm"],
  speechRecognizerAliases: ["primary-stt"],
  speechSynthesizerAliases: ["primary-tts"],
  defaultLanguageModelAlias: "primary-llm",
  defaultSpeechRecognizerAlias: "primary-stt",
  defaultSpeechSynthesizerAlias: "primary-tts",
  defaultModelKey: "scripted-alpha",
  models: [
    {
      key: "scripted-alpha",
      displayName: "Scripted Alpha",
      supportedReasoningEfforts: ["low", "medium", "high"],
      defaultReasoningEffort: "medium"
    },
    {
      key: "scripted-beta",
      displayName: "Scripted Beta",
      supportedReasoningEfforts: [],
      defaultReasoningEffort: null
    }
  ],
  interruptionClassifiers: ["heuristic"]
};


const storedCandidate = {
  schemaVersion: 2,
  definitionId: "examiner",
  identity: { name: "Alex", role: "Examiner", description: "Practice speaking.", tone: "Calm" },
  goals: ["Assess the speaker"],
  agentInstructions: "Stored body",
  behaviorPolicy: {
    interruptionStyle: "acknowledgeThenContinue",
    acknowledgeInterruption: false,
    avoidUnsupportedClaims: false
  },
  conversationPolicy: {
    responseLength: "concise",
    askOneQuestionAtATime: false,
    language: "auto",
    maxOutputTokens: 256
  },
  initiativePolicy: {
    enabled: false,
    silenceThresholdMs: 3000,
    cooldownMs: 15000,
    maxPerSilencePeriod: 1,
    triggers: ["longSilence"],
    maxConsecutiveProactiveTurns: 1,
    maxSilentEvaluations: 8,
    maxInactivityMs: 900000
  },
  modelDefaults: { catalogKey: "scripted-alpha", reasoningEffort: "low" },
  providerPreferences: {
    languageModel: "primary-llm",
    speechRecognizer: null,
    speechSynthesizer: null,
    interruptionClassifier: "heuristic"
  },
  voice: { enabled: false, voiceId: "verse", speakingRate: 1 },
  memoryPolicy: {
    sessionMemory: false,
    identityUserPromotion: false,
    identityUserRetrieval: false,
    userPromotion: false,
    userRetrieval: false
  },
  triggerPolicy: {
    enabled: false,
    allowUserScheduling: false,
    allowOneShot: false,
    allowDaily: false,
    allowWeekly: false,
    allowIndefiniteRecurrence: false,
    maxActiveRegistrations: 4,
    oneShotHorizonDays: 30,
    minRecurrenceDays: 1,
    allowedSourceKinds: ["schedule"],
    allowFixedInterval: false,
    minFixedIntervalSeconds: 60
  },
  metadata: { owner: "kept" },
  environment: {
    harness: ["examiner-turn-taking"],
    toolAllowlist: [],
    knowledgeSources: [],
    workspace: { templateId: "" },
    attachments: { allowUnreadUnsupportedTypes: false }
  },
  untouchedMarker: { nested: "preserve-me" }
};

// Exercise the controlled form without loading inventory, version tables, resources,
// and publication gates. Canonical tenant editor tests cover save and revision behavior.
async function renderCandidate(candidate: DefinitionCandidate = storedCandidate) {
  vi.mocked(listAdminAuthoringOptions).mockResolvedValue(authoringOptions);
  const changed = vi.fn<(candidate: DefinitionCandidate) => void>();
  function ControlledEditor() {
    const [value, setValue] = useState(candidate);
    const [view, setView] = useState<"form" | "json">("form");
    return <DefinitionCandidateEditor
      candidate={value} view={view} jsonText={candidateToJson(value)} busy={false}
      onCandidateChange={next => { changed(next); setValue(next); }}
      onJsonTextChange={text => {
        const parsed = applyCandidateJson(text);
        if (parsed.ok) { changed(parsed.candidate); setValue(parsed.candidate); }
      }}
      onViewChange={setView}
    />;
  }
  await act(async () => { render(<ControlledEditor />); });
  openSettings();
  expect(await screen.findByTitle("Scripted Alpha")).toBeInTheDocument();
  return changed;
}

function openSettings(...sections: string[]) {
  fireEvent.click(exposedRoles().getByRole("tab", { name: "Settings" }));
  const settings = document.querySelector<HTMLElement>(".admin-definition-settings > .ant-collapse")!;
  const names = sections.length ? sections : ["Agent instructions", "Model defaults"];
  for (const name of names) {
    const header = exposedRoles(settings).getByRole("button", { name });
    if (header.getAttribute("aria-expanded") === "false") fireEvent.click(header);
  }
}

function setText(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe("definition candidate editor", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it.each(["coreEvent", "applicationEvent"])("preserves restricted %s authority through unrelated editing and JSON round trips", async kind => {
    const candidate = structuredClone(storedCandidate);
    candidate.triggerPolicy.allowedSourceKinds = [kind];
    const changed = await renderCandidate(candidate);
    openSettings("Trigger restrictions");
    expect(exposedRoles().getByRole("checkbox", { name: "Events" })).toHaveAttribute("aria-checked", "mixed");
    fireEvent.click(exposedRoles().getByRole("tab", { name: "Profile" }));
    setText("Definition name", "Edited name");
    openSettings("Trigger restrictions");
    fireEvent.click(screen.getByText("Advanced JSON", { selector: ".ant-segmented-item-label" }));
    const json = exposedRoles().getByRole("textbox", { name: "Advanced JSON" });
    const value = JSON.parse((json as HTMLTextAreaElement).value);
    expect(value.triggerPolicy.allowedSourceKinds).toEqual([kind]);
    value.triggerPolicy.allowedSourceKinds = ["schedule", kind];
    fireEvent.change(json, { target: { value: JSON.stringify(value) } });
    fireEvent.click(screen.getByText("Form", { selector: ".ant-segmented-item-label" }));
    expect(exposedRoles().getByRole("checkbox", { name: "Schedule" })).toBeChecked();
    expect(exposedRoles().getByRole("checkbox", { name: "Events" })).toHaveAttribute("aria-checked", "mixed");
    expect(changed.mock.lastCall?.[0]).toMatchObject({ triggerPolicy: { allowedSourceKinds: ["schedule", kind] } });
  });

  it("validates and adds a Skill while preserving inherited content", async () => {
    const initial = {
      ...storedCandidate,
      skills: [
        {
          id: "order.lookup",
          name: "Order lookup",
          description: "Look up an order",
          procedure: "ORDER_PROCEDURE",
          projection: "OnDemand", defaultEnabled: true,
          requiredCapabilities: ["web.search"],
          resourcePaths: []
        }
      ]
    };
    const changed = vi.fn<(candidate: DefinitionCandidate) => void>();
    function ControlledSkills() {
      const [value, setValue] = useState<DefinitionCandidate>(initial);
      return <DefinitionSkillsSection candidate={value} busy={false} readOnly={false}
        onChange={next => { changed(next); setValue(next); }} />;
    }
    await act(async () => { render(<App><ControlledSkills /></App>); });

    expect(screen.getByText(/Required capabilities are requirements, not grants/)).toBeInTheDocument();
    fireEvent.click(exposedRoles().getByRole("button", { name: "Details" }));
    expect(await screen.findByText("ORDER_PROCEDURE")).toBeVisible();
    fireEvent.click(exposedRoles().getByRole("button", { name: "Close inspection" }));
    fireEvent.click(exposedRoles().getByRole("button", { name: "Add skill" }));
    fireEvent.click(exposedRoles().getByRole("button", { name: "Save Skill" }));
    expect(await screen.findByText("Please enter Skill name", {}, { timeout: 5000 })).toBeVisible();
    setText("Skill ID", "refund.handle");
    setText("Skill name", "Refund");
    setText("Procedure", "REFUND_PROCEDURE");
    setText("Description", "refund, return");
    setText("Required capabilities", "workspace.read, chat.respond");
    setText("Resource paths", "notes/refund.md");
    fireEvent.click(exposedRoles().getByRole("button", { name: "Save Skill" }));
    await waitFor(() => expect(screen.getByText("Refund")).toBeInTheDocument());
    const saved = candidateForPersistence(changed.mock.lastCall![0]) as {
      skills: Array<{ id: string; procedure: string; requiredCapabilities: string[]; resourcePaths: string[] }>;
    };
    expect(saved.skills[0]).toEqual(initial.skills[0]);
    expect(saved.skills[1]).toMatchObject({ id: "refund.handle", procedure: "REFUND_PROCEDURE",
      requiredCapabilities: ["workspace.read", "chat.respond"], resourcePaths: ["notes/refund.md"] });
  });

  it("round-trips Skills through form and JSON without changing inherited content", async () => {
    const inherited = { id: "order.lookup", name: "Order lookup", description: "Look up an order",
      procedure: "ORDER_PROCEDURE", projection: "OnDemand", defaultEnabled: true,
      requiredCapabilities: ["web.search"], resourcePaths: [] };
    const refund = { id: "refund.handle", name: "Refund", description: "refund, return",
      procedure: "REFUND_PROCEDURE", projection: "OnDemand", defaultEnabled: true,
      requiredCapabilities: ["workspace.read", "chat.respond"], resourcePaths: ["notes/refund.md"] };
    const changed = await renderCandidate({ ...storedCandidate, skills: [inherited, refund] });
    fireEvent.click(exposedRoles().getByRole("radio", { name: "Advanced JSON" }));
    const json = exposedRoles().getByRole("textbox", { name: "Advanced JSON" }) as HTMLTextAreaElement;
    expect(json.value).toContain("REFUND_PROCEDURE");
    expect(json.value).toContain("ORDER_PROCEDURE");
    fireEvent.change(json, { target: { value: json.value.replace("REFUND_PROCEDURE", "REFUND_PROCEDURE_EDITED") } });
    fireEvent.click(exposedRoles().getByRole("radio", { name: "Form" }));
    openSettings();

    const saved = candidateForPersistence(changed.mock.lastCall![0]) as { skills: unknown[] };
    expect(saved.skills).toEqual([inherited, { ...refund, procedure: "REFUND_PROCEDURE_EDITED" }]);
  });

  it("orders descending model efforts without rewriting the selected candidate", async () => {
    const descending = { ...authoringOptions, models: authoringOptions.models.map(model => ({ ...model,
      supportedReasoningEfforts: [...model.supportedReasoningEfforts].reverse() })) };
    vi.mocked(listAdminAuthoringOptions).mockResolvedValue(descending);
    const changed = vi.fn();
    render(<DefinitionCandidateEditor candidate={storedCandidate} view="form" jsonText={candidateToJson(storedCandidate)}
      busy={false} onCandidateChange={changed} onJsonTextChange={vi.fn()} onViewChange={vi.fn()} />);
    openSettings();
    await screen.findByTitle("Scripted Alpha");
    fireEvent.mouseDown(screen.getByLabelText("Reasoning"));
    await exposedRoles().findByRole("listbox");
    expect([...document.querySelectorAll(".ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-content")]
      .map(option => option.textContent)).toEqual(["Low", "Medium", "High"]);
    expect(changed).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTitle("High"));
    expect(changed.mock.lastCall![0].modelDefaults.reasoningEffort).toBe("high");
  });

  it("emits a voice-enabled candidate with default speech aliases", async () => {
    const changed = await renderCandidate();

    openSettings("Voice", "Provider preferences");
    fireEvent.click(screen.getByLabelText("Voice enabled"));
    expect(screen.getByTitle("primary-stt")).toBeInTheDocument();
    expect(screen.getByTitle("primary-tts")).toBeInTheDocument();

    const saved = changed.mock.lastCall?.[0] as {
      voice?: { enabled?: boolean };
      providerPreferences?: { speechRecognizer?: string; speechSynthesizer?: string };
    };
    expect(saved.voice?.enabled).toBe(true);
    expect(saved.providerPreferences).toMatchObject({
      speechRecognizer: "primary-stt",
      speechSynthesizer: "primary-tts"
    });
  });

  it("round-trips form and JSON without dropping untouched fields", async () => {
    await renderCandidate();

    fireEvent.click(exposedRoles().getByRole("tab", { name: "Profile" }));
    setText("Definition name", "Guide");
    fireEvent.click(exposedRoles().getByRole("radio", { name: "Advanced JSON" }));
    const json = exposedRoles().getByRole("textbox", { name: "Advanced JSON" }) as HTMLTextAreaElement;
    const edited = JSON.parse(json.value) as {
      identity: { name: string; role: string; tone: string };
      voice: { voiceId: string };
      metadata: { owner: string };
      modelDefaults: { catalogKey: string };
    };
    expect(edited.identity.name).toBe("Guide");
    expect(edited.voice.voiceId).toBe("verse");
    expect(edited.metadata.owner).toBe("kept");
    edited.identity.role = "Coach";
    fireEvent.change(json, { target: { value: JSON.stringify(edited, null, 2) } });
    fireEvent.click(exposedRoles().getByRole("radio", { name: "Form" }));
    openSettings();

    expect(screen.getByLabelText("Definition name")).toHaveValue("Guide");
    expect(screen.getByLabelText("Definition role")).toHaveValue("Coach");
    expect(screen.getByLabelText("Definition tone")).toHaveValue("Calm");
    openSettings("Voice");
    expect(screen.getByLabelText("Voice id")).toHaveValue("verse");
    expect(screen.getByTitle("Scripted Alpha")).toBeInTheDocument();
  });
});

describe("authoring options failure", () => {
  function renderEditor() {
    render(
      <DefinitionCandidateEditor
        candidate={storedCandidate}
        view="form"
        jsonText="{}"
        busy={false}
        onCandidateChange={() => undefined}
        onJsonTextChange={() => undefined}
        onViewChange={() => undefined}
      />
    );
  }

  it("shows error details on the fixed warning when the response includes an id", async () => {
    const failure = new Error("Admin authoring options failed (500)");
    failure.name = "AdminRequestError";
    Object.assign(failure, { diagnosticId: "019944af-0008-7000-8000-0000000000e2" });
    vi.mocked(listAdminAuthoringOptions).mockRejectedValueOnce(failure);

    renderEditor();
    fireEvent.click(exposedRoles().getByRole("tab", { name: "Settings" }));

    await waitFor(() => {
      expect(screen.getByText(/Authoring options could not be loaded/)).toBeInTheDocument();
    });
    expect(exposedRoles().getByRole("button", { name: "Error details" })).toBeInTheDocument();
  });

  it("keeps the warning without error details when the failure has no id", async () => {
    vi.mocked(listAdminAuthoringOptions).mockRejectedValueOnce(new Error("offline"));

    renderEditor();
    fireEvent.click(exposedRoles().getByRole("tab", { name: "Settings" }));

    await waitFor(() => {
      expect(screen.getByText(/Authoring options could not be loaded/)).toBeInTheDocument();
    });
    expect(screen.queryByRole("button", { name: "Error details" })).not.toBeInTheDocument();
  });
});

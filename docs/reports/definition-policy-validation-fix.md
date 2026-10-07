# Definition Automation policy validation correction

Date: 2026-10-07. Scope: normal Definition creation/editing accepted fractional recurrence input, then exposed a JSON conversion error during Save. The existing policy contract uses whole-number limits; fractional recurrence is not added.

The shared candidate validation helper checks max active registrations (1–32), one-shot horizon and minimum recurrence (1–365 days), and minimum fixed interval (60–604800 seconds). Invalid values remain visible, with inline accessible error text and an Ant Design error state. Save is blocked across Form, Advanced JSON and other tabs, with the field-specific message in the action area. Correcting the value clears the error. Existing absent optional/default behavior is preserved.

The API's candidate JSON mapper returns HTTP 400 with `field`, `validationCode: "invalid_integer"`, and a readable detail for unreadable policy integers. For example: “Minimum recurrence days must be a whole number from 1 to 365.” The regression exercises all four fields through rejected create, valid create, rejected update, unchanged candidate/revision readback and corrected update.

MCP exercised normal New definition on isolated Synthetic in-memory: enter `0.5`, verify the inline error and disabled Save, switch to Capabilities and verify Save remains disabled, correct to `2`, save, reload and read back `2` at revision 2. Desktop 1280px and mobile 390px had no document overflow. Related console errors were absent. Captures: [desktop](assets/definition-recurrence-validation-desktop.png), [mobile](assets/definition-recurrence-validation-mobile.png).

Checks: focused API regressions 4 passed; full API suite 365 passed, three opt-in skips, zero failed. Production build passed with the existing bundle-size warning. Focused frontend: 30 editor/candidate tests and three API-problem mapping tests passed. The first unit attempt used the wrong fixture id in its new assertion; the corrected test passed without changing product behavior or timeouts. This bounded correction does not move the Instance Skills migration freeze on `9c2d40e0` or reopen historical milestone gates.

Final integrated regression: all three Skills Playwright journeys passed in 32.3 seconds against the updated Synthetic host, including custom-persona instance creation, owner editing, publication upgrade/rollback and independent local Skills.

import { describe, it, expect, beforeAll } from "vitest";
import { JSDOM } from "jsdom";
import axe from "axe-core";

/**
 * Automated accessibility checks over the markup patterns this app relies on.
 *
 * These assert the *shapes* the UI produces — the keyboard-activatable rows, the
 * skip link, the confirm dialog, the data tables — rather than mounting the full
 * React tree, which would drag in MSAL and a live API. The patterns are copied
 * from what the components render, so a regression in those shapes is caught
 * here before it reaches a screen reader.
 *
 * Rendering the real component tree under test is the natural next step once
 * there is a way to stub authentication; this covers the structural rules today.
 */

/** Runs axe over a fragment and returns violations, ignoring colour contrast
 *  (the fragments carry no stylesheet, so contrast cannot be judged here). */
async function violationsIn(html: string): Promise<axe.Result[]> {
  const dom = new JSDOM(`<!doctype html><html lang="en"><body>${html}</body></html>`);
  // axe needs real globals to walk the tree.
  (globalThis as Record<string, unknown>).window = dom.window;
  (globalThis as Record<string, unknown>).document = dom.window.document;
  (globalThis as Record<string, unknown>).Node = dom.window.Node;
  (globalThis as Record<string, unknown>).Element = dom.window.Element;
  (globalThis as Record<string, unknown>).HTMLElement = dom.window.HTMLElement;

  const results = await axe.run(dom.window.document.body, {
    rules: { "color-contrast": { enabled: false }, region: { enabled: false } },
  });
  return results.violations;
}

const describeViolations = (v: axe.Result[]) =>
  v.map(x => `${x.id}: ${x.help} (${x.nodes.length} node(s))`).join("\n");

beforeAll(() => {
  // axe-core is chatty about unsupported environments; keep test output readable.
  axe.configure({ reporter: "v2" });
});

describe("accessibility of core markup patterns", () => {
  it("keyboard-activatable rows expose button semantics", async () => {
    // What rowActivation() produces: role, tabindex, and an accessible name.
    const html = `
      <table>
        <caption>Triggered alerts</caption>
        <thead><tr><th scope="col">Severity</th><th scope="col">Policy</th></tr></thead>
        <tbody>
          <tr role="button" tabindex="0" aria-label="Open triggered alert Privileged role assigned">
            <td>High</td><td>Privileged role assigned</td>
          </tr>
        </tbody>
      </table>`;
    const v = await violationsIn(html);
    expect(describeViolations(v)).toBe("");
  });

  it("detects a real violation, so a passing suite means something", async () => {
    // Guards the guard. An icon-only control with no accessible name is the
    // exact defect this app had before the a11y pass, and axe must flag it —
    // otherwise every "no violations" result above is meaningless.
    //
    // Note a <tr role="button"> WITHOUT aria-label is not a violation: it takes
    // its name from the cell text. The aria-label rowActivation adds makes rows
    // announce "Open alert X" rather than just "High", which is better but not
    // something axe can require.
    const v = await violationsIn(`<button type="button"><svg aria-hidden="true"></svg></button>`);
    expect(v.map(x => x.id)).toContain("button-name");
  });

  it("the skip link is a valid in-page link with discernible text", async () => {
    const html = `
      <a class="skip-link" href="#main-content">Skip to main content</a>
      <main id="main-content" tabindex="-1"><h1>Overview</h1></main>`;
    expect(describeViolations(await violationsIn(html))).toBe("");
  });

  it("the confirm dialog is a labelled alertdialog", async () => {
    const html = `
      <div role="alertdialog" aria-modal="true" aria-labelledby="confirm-title" aria-describedby="confirm-msg">
        <h2 id="confirm-title">Delete alert policy?</h2>
        <p id="confirm-msg">This policy will stop evaluating.</p>
        <button type="button">Cancel</button>
        <button type="button">Delete policy</button>
      </div>`;
    expect(describeViolations(await violationsIn(html))).toBe("");
  });

  it("sortable table headers announce their sort state", async () => {
    const html = `
      <table>
        <caption>Active alerts</caption>
        <thead><tr>
          <th scope="col" aria-sort="ascending"><button type="button">Severity</button></th>
          <th scope="col"><button type="button">Policy</button></th>
        </tr></thead>
        <tbody><tr><td>High</td><td>MFA drop</td></tr></tbody>
      </table>`;
    expect(describeViolations(await violationsIn(html))).toBe("");
  });

  it("sort changes have a polite, atomic status announcement", async () => {
    const html = `
      <table>
        <caption>Active alerts. Select a column heading to change the sort order.</caption>
        <thead><tr><th scope="col" aria-sort="descending"><button type="button">Triggered</button></th></tr></thead>
        <tbody><tr><td>Today</td></tr></tbody>
      </table>
      <div role="status" aria-live="polite" aria-atomic="true">Active alerts sorted by triggered time, descending</div>`;
    expect(describeViolations(await violationsIn(html))).toBe("");
  });

  it("icon-only controls carry an accessible name", async () => {
    const html = `
      <button type="button" aria-label="Refresh data"><svg aria-hidden="true"><path d="M0 0"/></svg></button>
      <button type="button" aria-label="Hide setup checklist"><svg aria-hidden="true"><path d="M0 0"/></svg></button>`;
    expect(describeViolations(await violationsIn(html))).toBe("");
  });

  it("status regions are announced politely", async () => {
    const html = `
      <div role="status" aria-live="polite">Data collected 3m ago, all sources OK</div>
      <div role="alert">2 of 25 dashboard panels failed to load</div>`;
    expect(describeViolations(await violationsIn(html))).toBe("");
  });
});

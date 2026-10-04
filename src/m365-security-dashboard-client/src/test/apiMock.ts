import { vi } from "vitest";

/**
 * Stubs `fetch` for component tests. Pages talk to the API only through
 * apiFetch/call, which use global fetch, so no component code changes are
 * needed to test them. Routes match on "METHOD /path" (query string ignored)
 * or on "/path" for any method; the first match wins. Unmatched calls fail the
 * request with 404 and are recorded so a test can assert nothing unexpected
 * was called.
 */
export type MockReply = { status?: number; body?: unknown };
export type MockRoute = MockReply | ((req: { url: string; method: string; body: unknown }) => MockReply);

export interface ApiMock {
  calls: { method: string; path: string; body: unknown; headers: Headers }[];
  unmatched: string[];
  on(route: string, reply: MockRoute): ApiMock;
}

export function mockApi(routes: Record<string, MockRoute> = {}): ApiMock {
  const table: [string, MockRoute][] = Object.entries(routes);
  const mock: ApiMock = {
    calls: [],
    unmatched: [],
    on(route, reply) { table.unshift([route, reply]); return mock; },
  };

  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
    const method = (init?.method ?? "GET").toUpperCase();
    const path = new URL(url, "http://localhost").pathname;
    const body = typeof init?.body === "string" ? safeJson(init.body) : init?.body ?? null;
    mock.calls.push({ method, path, body, headers: new Headers(init?.headers) });

    const hit = table.find(([key]) => key === `${method} ${path}` || key === path);
    if (!hit) {
      mock.unmatched.push(`${method} ${path}`);
      return json({ error: "unmocked" }, 404);
    }
    const reply = typeof hit[1] === "function" ? hit[1]({ url, method, body }) : hit[1];
    return json(reply.body ?? {}, reply.status ?? 200);
  }));

  return mock;
}

function json(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function safeJson(s: string): unknown {
  try { return JSON.parse(s); } catch { return s; }
}

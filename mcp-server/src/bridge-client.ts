import dotenv from "dotenv";

// stdio 是 MCP 协议通道，环境加载不得向 stdout 打印提示。
dotenv.config({ quiet: true });
export const BRIDGE_URL = (process.env.CS2_BRIDGE_URL ?? "http://127.0.0.1:8642").replace(/\/+$/, "");

export class BridgeError extends Error {
  constructor(message: string, public readonly mayHaveApplied = false, public readonly safeToRetry = false,
    public readonly status?: number, public readonly validationErrors?: unknown[]) { super(message); }
}

export function isMutation(path: string): boolean {
  const url = new URL(path, "http://127.0.0.1");
  if (url.searchParams.get("dryRun") === "true" && ["/build/place", "/build/road/connect", "/build/terrain", "/build/building/upgrade",
    "/build/place/shoreline", "/build/place/roadside", "/build/extractor-area/edit"].includes(url.pathname)) return false;
  return (url.pathname === "/game/milestone-popup" && url.searchParams.get("dismiss") === "true")
    || url.pathname.startsWith("/build/") || url.pathname === "/mods/anarchy/normal-construction"
    || url.pathname === "/mods/anarchy/props/refresh"
    || /\/(set|purchase|buy|save|control|run|create|delete|place)$/.test(url.pathname);
}

function bridgeAddress(base: string, path: string): URL {
  const origin = new URL(base);
  if (origin.protocol !== "http:" || !["127.0.0.1", "localhost", "[::1]"].includes(origin.hostname)
    || origin.username || origin.password || origin.pathname !== "/" || origin.search || origin.hash)
    throw new BridgeError("CS2_BRIDGE_URL must be a credential-free loopback HTTP origin; no request was sent.");
  if (!path.startsWith("/") || path.startsWith("//") || path.includes("\\"))
    throw new BridgeError("Invalid local bridge path; no request was sent.");
  // URLSearchParams 的 '+' 表示空格；字面 '+' 已经编码为 %2B。
  return new URL(path.replace(/\+/g, "%20"), origin);
}

export async function bridgeFetch(path: string, timeoutMs: number, base = BRIDGE_URL, request: Pick<RequestInit,'method'|'body'|'headers'> = {}): Promise<Response> {
  const url = bridgeAddress(base, path);
  try {
    // POST matches the bridge's mutating-route contract, including previews.
    // Classify the pathname here: dryRun describes outcome, not HTTP method.
    const headers = new Headers(request.headers);
    if (process.env.CS2MCP_TOKEN) headers.set("X-CS2MCP-Token", process.env.CS2MCP_TOKEN);
    return await fetch(url, { ...request, headers,
      method: request.method ?? ((isMutation(url.pathname) || isMutation(url.pathname + url.search)) ? "POST" : "GET"),
      signal: AbortSignal.timeout(timeoutMs), redirect: "error" });
  } catch (error) {
    throw new BridgeError(`Cannot reach the CS2 bridge (${(error as Error).message}). ` +
      (isMutation(path) ? "Operation outcome is unknown; inspect the city before retrying." : "Check the game and CS2MCP mod."),
    isMutation(path));
  }
}

export async function bridgeJson(path: string, timeoutMs = 12_000, base = BRIDGE_URL, request: Pick<RequestInit,'method'|'body'|'headers'> = {}): Promise<unknown> {
  const res = await bridgeFetch(path, timeoutMs, base, request);
  let payload: unknown;
  try { payload = JSON.parse(await res.text()); }
  catch { throw new BridgeError(`CS2 bridge returned an incomplete or invalid JSON response (HTTP ${res.status}); inspect before retrying.`, isMutation(path), false, res.status); }
  if (!res.ok) {
    const failure = payload as { error?: string; mayHaveApplied?: boolean; validationErrors?: unknown };
    const message = String(failure?.error ?? `bridge returned HTTP ${res.status}`);
    const busy = res.status === 409 && (/^another (build|road|transit) operation is in progress, retry shortly$/i.test(message)
      || message === "another construction operation is in progress");
    const mayHaveApplied = isMutation(path) && (failure?.mayHaveApplied === true || res.status >= 500);
    throw new BridgeError(message, mayHaveApplied, busy && !mayHaveApplied, res.status,
      Array.isArray(failure?.validationErrors) ? failure.validationErrors : undefined);
  }
  return payload;
}

export function jsonResult(payload: unknown) {
  return { content: [{ type: "text" as const, text: JSON.stringify(payload, null, 2) }] };
}

export function errorResult(error: unknown) {
  const bridge = error instanceof BridgeError ? error : undefined;
  return { ...jsonResult({ error: error instanceof Error ? error.message : String(error),
    mayHaveApplied: bridge?.mayHaveApplied ?? false, safeToRetry: bridge?.safeToRetry ?? false, status: bridge?.status,
    validationErrors: bridge?.validationErrors }), isError: true };
}

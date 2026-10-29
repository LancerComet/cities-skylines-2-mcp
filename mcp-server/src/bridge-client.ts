import dotenv from "dotenv";

// stdio 是 MCP 协议通道，环境加载不得向 stdout 打印提示。
dotenv.config({ quiet: true });
export const BRIDGE_URL = (process.env.CS2_BRIDGE_URL ?? "http://127.0.0.1:8642").replace(/\/+$/, "");

const BRIDGE_TOKEN = process.env.CS2MCP_TOKEN?.trim() ?? "";

export class BridgeError extends Error {
  constructor(message: string, public readonly mayHaveApplied = false, public readonly safeToRetry = false,
    public readonly status?: number, public readonly validationErrors?: unknown[], public readonly failureDetails?: Record<string, unknown>) { super(message); }
}

export function isMutation(path: string): boolean {
  const url = new URL(path, "http://127.0.0.1");
  if (url.searchParams.get("dryRun") === "true" && ["/build/place", "/build/road/connect", "/build/road/replace", "/build/terrain", "/build/building/upgrade", "/build/building/rebuild", "/build/industry/rebuild",
    "/build/place/shoreline", "/build/place/roadside", "/build/extractor-area/edit", "/build/building/upgrade/relocate", "/transit/stops/relocate"].includes(url.pathname)) return false;
  return (url.pathname === "/game/milestone-popup" && url.searchParams.get("dismiss") === "true")
    || url.pathname.startsWith("/build/") || url.pathname === "/transit/stops/relocate" || url.pathname === "/mods/anarchy/normal-construction"
    || url.pathname === "/mods/anarchy/props/refresh"
    || /\/(set|purchase|buy|save|control|run|create|delete|place)$/.test(url.pathname);
}

/** 与HttpBridgeServer.IsMutatingPath一致；传输POST与是否实际应用分别判断。 */
export function requiresPost(path: string): boolean {
  const route = new URL(path, "http://127.0.0.1").pathname;
  return route === "/game/milestone-popup" || route.startsWith("/build/") || route.startsWith("/sim/")
    || /\/(set|purchase|buy|save|control|run|create|delete|place)$/.test(route)
    || ["/transit/stops/relocate", "/mods/anarchy/normal-construction", "/mods/anarchy/props/refresh"].includes(route);
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
    const headers = new Headers(request.headers);
    if (BRIDGE_TOKEN) headers.set("X-CS2MCP-Token", BRIDGE_TOKEN);
    return await fetch(url, { ...request, method: requiresPost(path) ? "POST" : request.method ?? "GET",
      headers, signal: AbortSignal.timeout(timeoutMs), redirect: "error" });
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
    const message = String((payload as { error?: string })?.error ?? `bridge returned HTTP ${res.status}`);
    const busy = res.status === 409 && (/^another (build|road|transit) operation is in progress, retry shortly$/i.test(message)
      || message === "another construction operation is in progress");
    // 保留原生预览的错误位置，便于调整几何；它不改变失败结果和重试边界。
    const validationErrors = (payload as { validationErrors?: unknown })?.validationErrors;
    const nativeApplied = (payload as { mayHaveApplied?: unknown })?.mayHaveApplied;
    const failure = payload as Record<string, unknown>;
    // 仅接受关闭协议中有严格状态证明的“未开始”；任意500的false不能掩盖未知应用。
    const stoppedBeforeHandler = failure?.code === "bridge_stopped" && nativeApplied === false
      && failure.safeToRetry === false && failure.handlerStarted === false
      && ((failure.failurePhase === "prequeue" && failure.enqueued === false)
        || (failure.failurePhase === "before_handler" && failure.enqueued === true));
    const outcomeUnknown = isMutation(path) && (nativeApplied === true || res.status >= 500 && !stoppedBeforeHandler);
    const details: Record<string, unknown> = {};
    if (failure?.code === "bridge_stopped" || failure?.code === "resource_budget" || failure?.code === "response_timeout") {
      for (const key of ["code","failurePhase","enqueued","handlerStarted","reason","resourceBudget","timeoutMs"])
        if (failure[key] !== undefined) details[key] = failure[key];
    }
    if (["transit_tool_interrupted","transit_tool_failed","transit_commit_unverified"].includes(String(failure?.code))) {
      for (const key of ["code","failurePhase","operation","stage","target"])
        if (failure[key] !== undefined) details[key] = failure[key];
    }
    throw new BridgeError(message, outcomeUnknown, busy && !outcomeUnknown, res.status,
      Array.isArray(validationErrors) ? validationErrors : undefined, Object.keys(details).length ? details : undefined);
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
    validationErrors: bridge?.validationErrors, ...bridge?.failureDetails }), isError: true };
}

import { BridgeError } from "./bridge-client.js";

/** Product releases follow upstream; the native-control wire contract has its own identity. */
export function assertBridgeCompatibility(
  ping: { version?: string; controlApiVersion?: unknown },
  legacyMinimum: number,
  label: string,
): void {
  if (ping.controlApiVersion !== undefined) {
    if (ping.controlApiVersion === 1) return;
    throw new BridgeError(`${label} requires control API 1; received ${String(ping.controlApiVersion)}. No operation was submitted.`);
  }

  // Older published builds predate the explicit contract. Keep their original
  // minima so renumbering never makes a bare upstream 0.9.0 look compatible.
  const match = ping.version?.match(/^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$/);
  const parts = match?.slice(1).map(Number);
  if (parts && parts[0] * 1000000 + parts[1] * 1000 + parts[2] >= legacyMinimum) return;
  throw new BridgeError(`${label} requires control API 1 or a compatible historical bridge; received ${ping.version ?? "unknown"}. No operation was submitted.`);
}

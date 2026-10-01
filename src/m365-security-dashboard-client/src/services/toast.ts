import { getActiveClientName } from "./api";

export type ToastAction = { label: string; onAction: () => void | Promise<void> };
export type ToastEntry = { id: number; message: string; type?: "success" | "error" | "info"; action?: ToastAction };

let _addToast: ((t: Omit<ToastEntry, "id">) => void) | null = null;

/** Show a toast. Pass an action for undo-able operations —
 *  showToast("Alert resolved", "success", { label: "Undo", onAction: () => reopen(id) }). */
export function showToast(message: string, type: ToastEntry["type"] = "success", action?: ToastAction, opts?: { client?: string | null }): void {
  // In MSP mode every toast names the client it is about (MSP_V12_PLAN.md U5).
  // A caller acting on another client's data (the cross-client queue) names that
  // client explicitly instead of the selected one.
  const client = opts && "client" in opts ? opts.client ?? null : getActiveClientName();
  if (client && !message.startsWith(`${client}:`)) message = `${client}: ${message}`;
  if (_addToast) {
    _addToast({ message, type, action });
  } else {
    console.warn("Toast system not initialized yet. Message: ", message);
  }
}

export function registerToastHandler(handler: (t: Omit<ToastEntry, "id">) => void): () => void {
  _addToast = handler;
  return () => {
    _addToast = null;
  };
}

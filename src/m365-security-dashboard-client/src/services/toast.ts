export type ToastAction = { label: string; onAction: () => void | Promise<void> };
export type ToastEntry = { id: number; message: string; type?: "success" | "error" | "info"; action?: ToastAction };

let _addToast: ((t: Omit<ToastEntry, "id">) => void) | null = null;

/** Show a toast. Pass an action for undo-able operations —
 *  showToast("Alert resolved", "success", { label: "Undo", onAction: () => reopen(id) }). */
export function showToast(message: string, type: ToastEntry["type"] = "success", action?: ToastAction): void {
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

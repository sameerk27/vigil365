export type ToastEntry = { id: number; message: string; type?: "success" | "error" | "info" };

let _toastId = 0;
let _addToast: ((t: Omit<ToastEntry, "id">) => void) | null = null;

export function showToast(message: string, type: ToastEntry["type"] = "success"): void {
  if (_addToast) {
    _addToast({ message, type });
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

/**
 * In-app confirmation dialogs, replacing native window.confirm().
 *
 * Native dialogs ignore the app theme, cannot be styled, are suppressible by the
 * browser ("prevent this page from creating additional dialogs"), and block the
 * event loop. For destructive actions in a security tool the prompt must always
 * appear and must state consequences clearly.
 *
 * Mirrors the toast.ts registration pattern: the host component registers a
 * handler, callers await a promise.
 */
export type ConfirmRequest = {
  title: string;
  message: string;
  /** Label for the confirming button. Say what happens: "Delete policy", not "OK". */
  confirmLabel?: string;
  cancelLabel?: string;
  /** Styles the confirm button as destructive. */
  danger?: boolean;
};

let _requestConfirm: ((r: ConfirmRequest) => Promise<boolean>) | null = null;

/** Ask the user to confirm. Resolves true when confirmed, false when cancelled. */
export function confirmAction(request: ConfirmRequest): Promise<boolean> {
  if (!_requestConfirm) {
    // Host not mounted — fail closed rather than performing a destructive action.
    console.warn("Confirm system not initialized; denying action:", request.title);
    return Promise.resolve(false);
  }
  return _requestConfirm(request);
}

export function registerConfirmHandler(handler: (r: ConfirmRequest) => Promise<boolean>): () => void {
  _requestConfirm = handler;
  return () => { _requestConfirm = null; };
}

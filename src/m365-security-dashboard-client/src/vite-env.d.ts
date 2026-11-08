/// <reference types="vite/client" />

/** Injected by vite.config.ts from package.json "version" at build time. */
declare const __APP_VERSION__: string;

// MSAL 5's popup redirect bridge is an exports subpath, which "moduleResolution": "Node"
// can't see; Vite resolves it at build time. Signature from its own types/redirect_bridge.
declare module "@azure/msal-browser/redirect-bridge" {
  export function broadcastResponseToMainFrame(navigationClient?: unknown): Promise<void>;
}

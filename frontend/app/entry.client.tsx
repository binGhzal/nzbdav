// Adapted from the @react-router/dev 7.18.1 default client entry.
// React Router is MIT licensed; see /THIRD_PARTY_NOTICES.md for provenance and notice text.
import { startTransition, StrictMode } from "react";
import { hydrateRoot } from "react-dom/client";
import { HydratedRouter } from "react-router/dom";

export function handleClientFrameworkError(_error: unknown): void {
  console.error("frontend_render_error");
}

startTransition(() => {
  hydrateRoot(
    document,
    <StrictMode>
      <HydratedRouter onError={handleClientFrameworkError} />
    </StrictMode>,
    {
      onCaughtError: handleClientFrameworkError,
      onRecoverableError: handleClientFrameworkError,
      onUncaughtError: handleClientFrameworkError,
    },
  );
});

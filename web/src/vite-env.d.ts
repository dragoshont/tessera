/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Optional override for the broker base URL. The SPA is served at the same
   *  origin as the API, so this defaults to '' (same-origin). Set it for
   *  `npm run dev` against a separately-running broker, e.g. http://127.0.0.1:8080. */
  readonly VITE_TESSERA_API_URL?: string
  /** Set to '1' for a no-backend preview: the app uses the in-memory fixtures
   *  client instead of the real broker. Leave unset for real deployments. */
  readonly VITE_TESSERA_DEMO?: string
}

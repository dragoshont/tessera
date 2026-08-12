# Desktop Architecture

Electron packages local Web assets under secure `app://tessera`. The renderer uses the shared React UI and HTTPS APIs at `https://tessera.hont.ro`.

Main process owns:

- system-browser OIDC Authorization Code + PKCE and `tessera://auth/callback`;
- encrypted `safeStorage` auth persistence;
- fixed API origin, external-link allowlist, notifications, native menu and focus shortcut;
- custom protocol/CSP and validated deep-link navigation.

Preload exposes only named typed operations. Desktop owns no product state or background scheduler.
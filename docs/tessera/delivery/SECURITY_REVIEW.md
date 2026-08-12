# Security Review

Implemented controls:

- canonical owner and deterministic Account credential references;
- Google OAuth state replay protection, PKCE, fixed endpoints, least scopes, custody, refresh and revoke;
- bounded Gmail MIME/history parsing, inert HTML text, no attachment/remote-image fetch, hash-only content Evidence;
- provider output described as untrusted in model policy and tool definitions;
- fixed RM connector and model gateway routes; no caller URL;
- separate RM connector/session per spouse and provider identity pinning;
- exact one-use Actions, account/target/payload binding, replay denial, provider verification and unknown-outcome reconciliation;
- Jobs receive explicit account/capability grants and no unattended Gmail send or healthcare write tool;
- non-root containers, read-only root filesystem, RWO PVC, one replica, no browser-worker ingress;
- secret scan and no real email/medical content in tests/docs.

Known deployment limitation: the homelab default namespace currently has an allow-all egress policy. The Tessera-specific egress policy is correct but does not provide effective network containment until that global policy is narrowed or Tessera moves to a dedicated namespace. Application-level fixed-origin checks remain active.

No CAPTCHA/MFA bypass, OTP storage, password capture, RM cookie copy, or live side effect occurred.
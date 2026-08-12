# Account Isolation

## Regina Maria

Each external Account stores a distinct canonical owner, deterministic credential reference, fixed connector ID, fixed operator endpoint, verified provider account ID, permissions, and capability bindings. The browser sends only the connector ID. Two Accounts cannot share a configured endpoint, and endpoint URLs are not caller-controlled.

Tests prove:

- two live Accounts require explicit `accountId` in Chat tool schemas;
- omission returns `account_ambiguous`;
- selecting A cannot return B scheduling data and vice versa;
- parked B remains `AUTH_REQUIRED`;
- identity drift fails closed;
- a Job uses only explicitly granted Account IDs;
- Actions bind account, target, and payload; account substitution invalidates availability/authorization.

## Gmail

Gmail Accounts are owner-bound to deterministic credential references and provider-verified email identity. Multiple Accounts require explicit selection. Gmail receives no RM connector state, and RM receives no Gmail credential.

External provider data remains Account-scoped in capability traces and Evidence.
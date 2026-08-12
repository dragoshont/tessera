# Regina Maria Provider Map

The deployed connector uses an unofficial, user-authorized patient-web contract behind an internal Streamable HTTP MCP server.

Scheduling tools used by Tessera:

- `rm_session_status`: refresh-chain liveness and deploy mutation-gate state;
- `rm_account_identity`: `MainProfile.UserRoleId` plus `FullName`, no medical data;
- `rm_list_appointments`: normalized scheduling logistics;
- `rm_search_slots`: normalized doctor/service/location/date/time candidates with opaque interval, physician, and service references;
- `rm_prepare_appointment`: non-mutating slot/service validation and payable/list price;
- `rm_create_appointment`: book or reschedule (`old_appointment_id`);
- `rm_cancel_appointment`: cancel.

The connector serializes calls around one rotating session and writes every token rotation to Key Vault. Tessera never reads or stores those cookies. Account A and B are separate deployments and session secrets. CAPTCHA/MFA remain human-only through the internal warm-browser/sessionkeeper flow.

Provider support is not claimed. UI/API changes may require connector maintenance. Critical shape drift fails closed.
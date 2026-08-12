# Dogfood Jobs

Useful Jobs to create through the deployed UI after real Accounts are connected:

1. Weekday Gmail attention brief: important unread messages and open follow-ups. Grants: selected Gmail Account plus `messages.search/get/threads.get`; no side effects.
2. Daily RM change monitor: upcoming appointments for each explicitly named authorized Account. Grants: both RM Accounts plus `appointments.list`; no writes.
3. Combined morning brief: Gmail attention, authorized appointment logistics, failed Jobs, and pending Actions. Grants remain explicit per Account/capability.

The Jobs UI derives Gmail/RM read grants only when the instruction names the provider or exact Account label. Multiple RM Accounts require the exact display label. No healthcare write capability and no Gmail send capability is granted by this flow. Jobs persist in SQLite, use leases, and recover without duplicate runs.
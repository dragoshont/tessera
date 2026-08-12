# Regina Maria Action Safety

All booking, reschedule, and cancel operations are `ExternalReversible` Tessera Actions. The connector's own mutation toggle is necessary but never sufficient.

The Action payload binds:

- canonical owner and exact ConnectedAccount;
- target appointment or booking scope;
- interval, physician, service, old appointment where applicable;
- human-readable doctor, specialty, service, location, date/time, mode, and displayed cost.

One-use authorization and payload hashing prevent replay, edits, and account swaps. The approval card shows the selected Account and scheduling details. Booking/reschedule uses a non-mutating provider preflight before proposal. After execution, Tessera re-lists appointments. Cancel succeeds only when the old ID is absent; book/reschedule succeeds only when the new appointment is found, and reschedule also verifies the old appointment is gone when the provider issued a new ID.

Timeout/5xx outcomes trigger provider re-read before any conclusion. If reconciliation cannot prove state, the Action becomes `RECONCILIATION_REQUIRED`; Tessera does not retry blindly.

No live medical write test may run without a user-approved safe target.
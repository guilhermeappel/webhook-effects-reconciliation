# The rule matrix

`ChargeRules.Decide` is checked against the implementation it was ported from. The file
`tests/WebhookInbox.Tests/Charges/decide-matrix.csv` holds the result of that implementation (the Python webhook
handler) for every combination of 8 local states, 7 payload statuses, the deleted flag on or off and 4 event types,
the flag and the event varying independently: 448 rows of states and results, plus whether the original re-evaluated
the plan. `ChargeRules.Decide` must reproduce each result, so a change that makes the example differ from the rule it
claims to port fails the build. The test also checks the file itself: the exact header, every value inside its
column's domain, and the full grid with no row missing or repeated.

The example reproduces the plan column only for rows where the charge changes. There are two deliberate differences,
and a test pins the size of each:

1. **A status the example does not model** (`CHARGEBACK_REQUESTED`). The original stores it and the example leaves the
   charge alone. Of the 32 rows that carry that status without the deleted flag, the original stores it in 21; in the
   other 11 a rule leaves the charge alone first (a refunded or a cancelled charge).
2. **An event that repeats the status the charge already has.** The original re-evaluates the plan, which can heal a
   plan whose earlier re-evaluation was lost, and the example asks for nothing. 36 rows repeat the status and leave
   the charge unchanged: in 28 the original re-evaluates the plan. In the other 8, a charge paid through the gateway
   arrives with the deleted flag, the deletion is ignored and does not revoke access, so there is nothing to
   re-evaluate.

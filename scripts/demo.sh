#!/usr/bin/env bash
# A short demonstration against one running backend (fake model mode, development customer CUST-1001):
# an answer, a denial, a write that waits for approval and then runs once, a model failure, and a refusal.
# Repeatable: the write is a support ticket (always eligible; each run creates the next TKT-number).
# Usage: scripts/demo.sh http://localhost:5180      (needs curl and jq; add -k for a local https endpoint)
set -euo pipefail

base="${1:?usage: scripts/demo.sh BASE_URL [curl options]}"
shift
curl_opts=("$@")
api="${base%/}/api/v1"

post() { curl -sS "${curl_opts[@]}" -X POST "$1" -H 'content-type: application/json' -d "$2"; }
turn() { post "$api/conversations/$1/turns" "{\"client_turn_id\":\"$(uuidgen)\",\"message\":\"$2\"}"; }
show() { printf '\n## %s\n> %s\n' "$1" "$2"; }

conversation=$(post "$api/conversations" '{}' | jq -r .conversation_id)
backend=$(curl -sS "${curl_opts[@]}" "$api/conversations/$conversation" | jq -r .backend)
echo "# Demo against the $backend backend ($base)"

show "Answer" "Where is my order ORD-1002?"
turn "$conversation" "Where is my order ORD-1002?" | jq -c '{outcome, route: .route.target, reply: .reply.text}'

show "Denial: another customer's order looks exactly like a missing one" "What's the status of ORD-2001?"
turn "$conversation" "What's the status of ORD-2001?" | jq -c '{outcome, reply_kind: .reply.kind, reply: .reply.text}'

show "Not eligible: checked before asking for approval" "Cancel order ORD-1001."
turn "$conversation" "Cancel order ORD-1001." | jq -c '{outcome, reply_kind: .reply.kind, operations: (.operations // [] | length)}'

ticket_request="Please open a support ticket: my smart fridge firmware shows error Z-77."
show "A write waits for approval" "$ticket_request"
proposal=$(turn "$conversation" "$ticket_request")
echo "$proposal" | jq -c '{outcome, pending: (.pending_approval | {kind, arguments, expires_at})}'
approval=$(echo "$proposal" | jq -r '.pending_approval.approval_id // empty')
operation=$(echo "$proposal" | jq -r '.pending_approval.operation_id // empty')
if [[ -z "$approval" || -z "$operation" ]]; then
  echo "No approval was proposed (outcome: $(echo "$proposal" | jq -r .outcome)); the rest of the demo needs one. Is the backend in fake mode?" >&2
  exit 1
fi

show "Approve it twice: the write runs once" "approve, approve"
for _ in 1 2; do
  post "$api/conversations/$conversation/approvals/$approval" "{\"operation_id\":\"$operation\",\"decision\":\"approve\"}" | jq -c '{outcome, turn_id, reply: .reply.text}'
done
curl -sS "${curl_opts[@]}" "$api/operations/$operation" | jq -c '{status, result}'

show "Changing your mind is refused" "reject (after approving)"
post "$api/conversations/$conversation/approvals/$approval" "{\"operation_id\":\"$operation\",\"decision\":\"reject\"}" | jq -c '{status, code}'

show "A model failure is a result, not a crash (fake-mode fault marker)" "Where is ORD-1002? [fake:model-error]"
turn "$conversation" "Where is ORD-1002? [fake:model-error]" | jq -c '{outcome, error: .error.code}'

show "Identity can't be smuggled in" "a turn request with a customer_id field"
post "$api/conversations/$conversation/turns" "{\"client_turn_id\":\"$(uuidgen)\",\"message\":\"Where is ORD-2001?\",\"customer_id\":\"CUST-1002\"}" | jq -c '{status, code}'

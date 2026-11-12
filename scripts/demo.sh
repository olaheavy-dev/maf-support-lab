#!/usr/bin/env bash
# A short demonstration against one running backend (fake model mode, development customer CUST-1001):
# an answer, a denial, a write that waits for approval and then runs once, a model failure, and a refusal.
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

show "A write waits for approval" "Please cancel order ORD-1003."
proposal=$(turn "$conversation" "Please cancel order ORD-1003.")
echo "$proposal" | jq -c '{outcome, pending: (.pending_approval | {kind, arguments, expires_at})}'
approval=$(echo "$proposal" | jq -r .pending_approval.approval_id)
operation=$(echo "$proposal" | jq -r .pending_approval.operation_id)

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

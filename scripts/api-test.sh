#!/usr/bin/env bash
# Regression test for the Matddns management API (/api/v1). Needs only bash and curl.
#
#   scripts/api-test.sh [base-url] [api-key]
#
# Defaults: base-url http://localhost:8080, api-key from $MATDDNS_API_KEY. Run it against a throw-away instance, e.g.
#   docker run -d --name matddns-test -p 18702:8080 -e MATDDNS_API_KEY=testkey matddns:local
#   scripts/api-test.sh http://localhost:18702 testkey
# Without an API key it only checks that the API refuses to answer (503), e.g. against an instance started without MATDDNS_API_KEY.
#
# The test creates objects named "apitest-*" (managedBy "apitest"), exercises upsert / idempotency / validation /
# deletion, and removes them again. A dummy Cloudflare token is used: nothing is sent to a real provider unless the
# updater runs (it will try, fail and log an error for the dummy token; that is expected).
set -u

BASE="${1:-http://localhost:8080}"
KEY="${2:-${MATDDNS_API_KEY:-}}"
PASS=0
FAIL=0
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

STATUS=""
OUT=""

# req METHOD PATH [BODY] [KEY]   -> sets STATUS and OUT (the response body)
req() {
  local method="$1" path="$2" body="${3:-}" key="${4-$KEY}"
  local args=(-s -o "$TMP" -w '%{http_code}' -X "$method" "$BASE$path")
  [ -n "$key" ] && args+=(-H "X-Api-Key: $key")
  [ -n "$body" ] && args+=(-H 'Content-Type: application/json' --data-binary "$body")
  STATUS="$(curl "${args[@]}")"
  OUT="$(cat "$TMP")"
}

ok()   { PASS=$((PASS + 1)); echo "  ok    $1"; }
fail() { FAIL=$((FAIL + 1)); echo "  FAIL  $1"; echo "        status=$STATUS body=$OUT"; }

expect_status() { # expect_status "what" code
  if [ "$STATUS" = "$2" ]; then ok "$1 -> $2"; else fail "$1: expected $2"; fi
}
expect_contains() { # expect_contains "what" substring
  if [[ "$OUT" == *"$2"* ]]; then ok "$1"; else fail "$1: expected to contain: $2"; fi
}
expect_missing() { # expect_missing "what" substring
  if [[ "$OUT" != *"$2"* ]]; then ok "$1"; else fail "$1: must not contain: $2"; fi
}
expect_equal() { # expect_equal "what" a b
  if [ "$2" = "$3" ]; then ok "$1"; else fail "$1: '$2' != '$3'"; fi
}

# the first "id" of an object response is the object's own id; entry ids are listed after "entries":[
obj_id()    { echo "$OUT" | sed -n 's/^{"id":"\([0-9a-f]\{32\}\)".*/\1/p'; }
entry_ids() { echo "$OUT" | sed 's/.*"entries":\[//' | grep -o '"id":"[0-9a-f]\{32\}"' | sed 's/.*:"\(.*\)"/\1/'; }
count_of()  { req GET /api/v1/status; echo "$OUT" | sed -n "s/.*\"$1\":\([0-9]*\).*/\1/p"; }

echo "Matddns management API test against $BASE"

# ---------------------------------------------------------------- authentication
echo "== authentication"
if [ -z "$KEY" ]; then
  req GET /api/v1/status
  expect_status "no key configured on the server: the API answers" 503
  expect_contains "503 carries an error message" '"error"'
  echo; echo "passed: $PASS, failed: $FAIL (key-less mode: only the 503 check ran)"
  [ "$FAIL" -eq 0 ]; exit $?
fi
req GET /api/v1/status "" ""
expect_status "no key" 401
req GET /api/v1/status "" "wrong-$KEY"
expect_status "wrong key" 401
req GET /api/v1/nothing-here
expect_status "unknown endpoint is a JSON 404, not a login redirect" 404

echo "== status"
req GET /api/v1/status
expect_status "status" 200
expect_contains "status app" '"app":"matddns"'
expect_contains "status apiVersion" '"apiVersion":1'
expect_contains "status version" '"version":'
expect_contains "status counts" '"counts":{"sources":'
N_SRC="$(count_of sources)"; N_DOM="$(count_of domains)"; N_RUL="$(count_of rules)"

# ---------------------------------------------------------------- create
echo "== create: source, domain group with two records, a rule per record"
req POST /api/v1/sources '{"name":"apitest-ip","kind":"PublicIp","intervalSeconds":60,"managedBy":"apitest"}'
expect_status "create PublicIp source" 200
expect_contains "source has managedBy" '"managedBy":"apitest"'
expect_contains "PublicIp source got its entry like in the UI" '"label":"Public IP"'
SRC="$(obj_id)"; SRC_ENTRY="$(entry_ids | head -1)"
[ -n "$SRC" ] && [ -n "$SRC_ENTRY" ] && ok "source id $SRC, entry id $SRC_ENTRY" || fail "could not read the source ids"

DOMAIN_BODY='{"name":"apitest-cloudflare","kind":"Cloudflare","managedBy":"apitest","cloudflare":{"apiToken":"dummy-token-0123456789","allowDynamic":true},"entries":[{"hostname":"apitest1.example.com","type":"A"},{"hostname":"apitest2.example.com","type":"A"}]}'
req POST /api/v1/domains "$DOMAIN_BODY"
expect_status "create Cloudflare domain group" 200
expect_contains "credentialsSet is true" '"credentialsSet":true'
expect_missing "the token is never returned" 'dummy-token'
expect_contains "records got zone and record name" '"recordName":"apitest1","domain":"example.com"'
DOM="$(obj_id)"; E1="$(entry_ids | sed -n 1p)"; E2="$(entry_ids | sed -n 2p)"
[ -n "$DOM" ] && [ -n "$E1" ] && [ -n "$E2" ] && [ "$E1" != "$E2" ] && ok "group $DOM, records $E1 $E2" || fail "could not read the domain ids"

RULE1_BODY="{\"domainGroupId\":\"$DOM\",\"domainEntryId\":\"$E1\",\"sourceEntryIdsInOrder\":[\"$SRC_ENTRY\"],\"onChange\":true,\"intervalSeconds\":300,\"managedBy\":\"apitest\"}"
RULE2_BODY="{\"domainGroupId\":\"$DOM\",\"domainEntryId\":\"$E2\",\"sourceEntryIdsInOrder\":[\"$SRC_ENTRY\"],\"onChange\":true,\"intervalSeconds\":300,\"managedBy\":\"apitest\"}"
req POST /api/v1/rules "$RULE1_BODY"; expect_status "create rule 1" 200; R1="$(obj_id)"
req POST /api/v1/rules "$RULE2_BODY"; expect_status "create rule 2" 200; R2="$(obj_id)"
[ -n "$R1" ] && [ -n "$R2" ] && [ "$R1" != "$R2" ] && ok "rules $R1 $R2" || fail "could not read the rule ids"
expect_equal "counts after create (sources)" "$(count_of sources)" "$((N_SRC + 1))"
expect_equal "counts after create (domains)" "$(count_of domains)" "$((N_DOM + 1))"
expect_equal "counts after create (rules)" "$(count_of rules)" "$((N_RUL + 2))"

# ---------------------------------------------------------------- idempotency
echo "== repeat the same calls: nothing changes, ids stay stable"
req POST /api/v1/sources '{"name":"apitest-ip","kind":"PublicIp","intervalSeconds":60,"managedBy":"apitest"}'
expect_equal "source id stable (upsert by name)" "$(obj_id)" "$SRC"
expect_equal "source entry id stable" "$(entry_ids | head -1)" "$SRC_ENTRY"
req POST /api/v1/domains "$DOMAIN_BODY"
expect_status "repeat domain group" 200
expect_equal "group id stable (upsert by name)" "$(obj_id)" "$DOM"
expect_equal "record 1 id stable (matched by hostname and type)" "$(entry_ids | sed -n 1p)" "$E1"
expect_equal "record 2 id stable" "$(entry_ids | sed -n 2p)" "$E2"
req POST /api/v1/rules "$RULE1_BODY"; expect_equal "rule 1 id stable (upsert by group + record)" "$(obj_id)" "$R1"
req POST /api/v1/rules "$RULE2_BODY"; expect_equal "rule 2 id stable" "$(obj_id)" "$R2"
expect_equal "counts unchanged (sources)" "$(count_of sources)" "$((N_SRC + 1))"
expect_equal "counts unchanged (domains)" "$(count_of domains)" "$((N_DOM + 1))"
expect_equal "counts unchanged (rules)" "$(count_of rules)" "$((N_RUL + 2))"

echo "== upsert by id, credentials kept when omitted"
req POST /api/v1/domains "{\"id\":\"$DOM\",\"name\":\"apitest-cloudflare\"}"
expect_status "update by id without entries and credentials" 200
expect_contains "credentials still stored" '"credentialsSet":true'
expect_equal "records untouched when 'entries' is omitted" "$(entry_ids | wc -l | tr -d ' ')" "2"

echo "== reads"
req GET /api/v1/sources;  expect_contains "GET sources lists it" '"name":"apitest-ip"'; expect_missing "GET sources: no secrets" '"token"'
req GET /api/v1/domains;  expect_contains "GET domains lists it" '"name":"apitest-cloudflare"'; expect_missing "GET domains: no token" 'dummy-token'
req GET /api/v1/rules;    expect_contains "GET rules lists rule 1" "\"id\":\"$R1\""; expect_contains "GET rules lists rule 2" "\"id\":\"$R2\""
STATE="$(curl -s "$BASE/api/state")"
[[ "$STATE" == *'"apitest-ip"'* ]] && ok "/api/state shows the source" || { OUT="$STATE"; fail "/api/state shows the source"; }
[[ "$STATE" == *'apitest1.example.com'* ]] && ok "/api/state shows the rule target" || { OUT="$STATE"; fail "/api/state shows the rule target"; }

# ---------------------------------------------------------------- validation
echo "== validation answers 400/404/409 with {\"error\":...}"
req POST /api/v1/sources 'not json';                                                     expect_status "invalid JSON" 400
req POST /api/v1/sources '{"kind":"PublicIp"}';                                          expect_status "source without a name" 400
req POST /api/v1/sources '{"name":"apitest-x","kind":"Nope"}';                           expect_status "unknown source kind" 400; expect_contains "lists the valid kinds" 'PublicIp'
req POST /api/v1/sources '{"name":"apitest-x"}';                                         expect_status "kind missing on create" 400
req POST /api/v1/sources '{"name":"apitest-x","kind":"Static","static":{"ip":"nope"}}';  expect_status "static source with an invalid IP" 400
req POST /api/v1/sources '{"name":"apitest-x","kind":"PublicIp","intervalSeconds":5}';   expect_status "source interval below 15" 400
req POST /api/v1/sources '{"name":"apitest-ip","kind":"Static","static":{"ip":"192.0.2.1"}}'; expect_status "changing the kind of a source" 400
req POST /api/v1/sources '{"id":"00000000000000000000000000000000","name":"x"}';         expect_status "unknown source id" 404
req POST /api/v1/domains '{"name":"apitest-x","kind":"DynDns","entries":[{"hostname":"a.example.com","type":"CNAME"}]}'
expect_status "CNAME on a DynDNS group" 400
req POST /api/v1/domains '{"name":"apitest-x","kind":"Cloudflare","entries":[{"hostname":"not a host","type":"A"}]}'
expect_status "invalid FQDN" 400
req POST /api/v1/domains '{"name":"apitest-x","kind":"Cloudflare","entries":[{"hostname":"a.example.com","type":"MX"}]}'
expect_status "invalid record type" 400
req POST /api/v1/domains '{"name":"apitest-x","kind":"Cloudflare","entries":[{"hostname":"a.example.com"},{"hostname":"A.example.com"}]}'
expect_status "the same record twice" 400
req GET /api/v1/domains; expect_missing "a rejected request stores nothing" '"name":"apitest-x"'
req POST /api/v1/rules "{\"domainGroupId\":\"$DOM\",\"domainEntryId\":\"nope\"}";                         expect_status "rule with an unknown record" 400
req POST /api/v1/rules "{\"domainGroupId\":\"$DOM\",\"domainEntryId\":\"$E1\",\"sourceEntryIdsInOrder\":[\"nope\"]}"; expect_status "rule with an unknown source entry" 400
req POST /api/v1/rules "{\"domainGroupId\":\"$DOM\",\"domainEntryId\":\"$E1\",\"intervalSeconds\":5}";   expect_status "rule interval below 15" 400
req POST /api/v1/rules "{\"domainGroupId\":\"$DOM\",\"domainEntryId\":\"$E1\",\"validationPort\":70000}"; expect_status "rule port out of range" 400

# ---------------------------------------------------------------- removing records
echo "== removing a record that a rule uses is refused (409); after the rule is gone it works"
SHORT_BODY="{\"name\":\"apitest-cloudflare\",\"kind\":\"Cloudflare\",\"entries\":[{\"id\":\"$E1\",\"hostname\":\"apitest1.example.com\",\"type\":\"A\"}]}"
req POST /api/v1/domains "$SHORT_BODY"
expect_status "remove record 2 while rule 2 uses it" 409
expect_contains "the message names the rule" "$R2"
req GET /api/v1/domains; expect_contains "nothing was removed" "$E2"
req DELETE "/api/v1/domains/$DOM"; expect_status "delete a group that rules use" 409
req DELETE "/api/v1/rules/$R2";    expect_status "delete rule 2" 204
req POST /api/v1/domains "$SHORT_BODY"
expect_status "remove record 2 once its rule is gone" 200
expect_contains "record 1 is kept" "$E1"
expect_missing "record 2 is gone" "$E2"
expect_equal "record 1 id stable" "$(entry_ids | sed -n 1p)" "$E1"
req GET /api/v1/rules; expect_contains "rule 1 is untouched" "\"id\":\"$R1\""; expect_missing "rule 2 is gone" "\"id\":\"$R2\""

echo "== run now"
req POST /api/v1/run
expect_status "run" 200
expect_contains "run answers ok" '{"ok":true}'

# ---------------------------------------------------------------- cleanup
echo "== cleanup"
req DELETE "/api/v1/rules/$R1";    expect_status "delete rule 1" 204
req DELETE "/api/v1/rules/$R1";    expect_status "delete rule 1 again" 404
req DELETE "/api/v1/domains/$DOM"; expect_status "delete the domain group" 204
req DELETE "/api/v1/sources/$SRC"; expect_status "delete the source" 204
expect_equal "counts back to the start (sources)" "$(count_of sources)" "$N_SRC"
expect_equal "counts back to the start (domains)" "$(count_of domains)" "$N_DOM"
expect_equal "counts back to the start (rules)" "$(count_of rules)" "$N_RUL"

echo
echo "passed: $PASS, failed: $FAIL"
[ "$FAIL" -eq 0 ]

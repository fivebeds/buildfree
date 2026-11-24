#!/usr/bin/env bash
# End-to-end check of the API. Run it against a running API:
#   API=http://localhost:5000 bash tests/smoke.sh
# It creates two test companies and checks that every endpoint works and that
# one company can never see or change the other company's data.
set -euo pipefail

API="${API:-http://localhost:5000}"
RUN="$(date +%s)$RANDOM"
PASS=0
BODY=""

# call METHOD PATH TOKEN JSON EXPECTED_STATUS
call() {
  local method="$1" path="$2" token="$3" json="$4" expected="$5"
  local args=(-s -o /tmp/smoke_body -w "%{http_code}" -X "$method" "$API$path" -H "Content-Type: application/json")
  [[ -n "$token" ]] && args+=(-H "Authorization: Bearer $token")
  [[ -n "$json" ]] && args+=(-d "$json")
  local status
  status="$(curl "${args[@]}")"
  BODY="$(cat /tmp/smoke_body)"
  if [[ "$status" != "$expected" ]]; then
    echo "FAIL: $method $path returned $status, expected $expected"
    echo "      body: $BODY"
    exit 1
  fi
  PASS=$((PASS + 1))
  echo "ok   $method $path -> $status"
}

field() { echo "$BODY" | jq -r "$1"; }

# upload PATH TOKEN EXPECTED_STATUS FILE... : sends files as the "photos" form field
upload() {
  local path="$1" token="$2" expected="$3"; shift 3
  local args=(-s -o /tmp/smoke_body -w "%{http_code}" -X POST "$API$path" -H "Authorization: Bearer $token")
  for f in "$@"; do args+=(-F "photos=@$f"); done
  local status
  status="$(curl "${args[@]}")"
  BODY="$(cat /tmp/smoke_body)"
  if [[ "$status" != "$expected" ]]; then
    echo "FAIL: POST $path (upload) returned $status, expected $expected"
    echo "      body: $BODY"
    exit 1
  fi
  PASS=$((PASS + 1))
  echo "ok   POST $path (upload $# file(s)) -> $status"
}

# getfile PATH TOKEN EXPECTED_STATUS : downloads to /tmp/smoke_file, sets CTYPE
getfile() {
  local path="$1" token="$2" expected="$3" out
  out="$(curl -s -o /tmp/smoke_file -w "%{http_code} %{content_type}" "$API$path" -H "Authorization: Bearer $token")"
  CTYPE="${out#* }"
  if [[ "${out%% *}" != "$expected" ]]; then
    echo "FAIL: GET $path returned ${out%% *}, expected $expected"
    exit 1
  fi
  PASS=$((PASS + 1))
  echo "ok   GET $path (file) -> ${out%% *}"
}

# Test files: a real 1x1 PNG, a tiny JPEG header, and a text file pretending to be a photo.
PNG=/tmp/smoke_photo.png
JPG=/tmp/smoke_photo.jpg
FAKE=/tmp/smoke_fake.jpg
echo "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=" | base64 -d > "$PNG"
printf '\xff\xd8\xff\xe0\x00\x10JFIF\x00\x01\x01\x00\x00\x01\x00\x01\x00\x00\xff\xd9' > "$JPG"
echo "this is not a photo" > "$FAKE"

echo "== Health check and the web app"
call GET /api/health "" "" 200
[[ "$(field .status)" == "ok" ]] || { echo "FAIL: health check should say ok: $BODY"; exit 1; }
PAGE="$(curl -s -w "\n%{http_code}" "$API/")"
[[ "${PAGE##*$'\n'}" == "200" && "$PAGE" == *"<title>BuildFree</title>"* ]] || { echo "FAIL: the web app isn't served at /"; exit 1; }
[[ "$(curl -s -o /dev/null -w "%{http_code}" "$API/estimates.js")" == "200" ]] || { echo "FAIL: estimates.js isn't served"; exit 1; }
PASS=$((PASS + 1)); echo "ok   web app served at /"

echo "== Sign up and log in"
call POST /api/auth/signup "" "{\"companyName\":\"Alpha Builders\",\"name\":\"Ana\",\"email\":\"alpha$RUN@example.com\",\"password\":\"changeme123\"}" 200
A="$(field .token)"
call POST /api/auth/signup "" "{\"companyName\":\"Bravo Builders\",\"name\":\"Ben\",\"email\":\"bravo$RUN@example.com\",\"password\":\"changeme123\"}" 200
B="$(field .token)"
call POST /api/auth/login "" "{\"email\":\"ALPHA$RUN@example.com\",\"password\":\"changeme123\"}" 200
call POST /api/auth/login "" "{\"email\":\"alpha$RUN@example.com\",\"password\":\"wrong-password\"}" 401
call POST /api/auth/signup "" "{\"companyName\":\"X\",\"name\":\"X\",\"email\":\"alpha$RUN@example.com\",\"password\":\"changeme123\"}" 409
call POST /api/auth/signup "" "{\"companyName\":\"X\",\"name\":\"X\",\"email\":\"short$RUN@example.com\",\"password\":\"short\"}" 400
call POST /api/auth/signup "" "{\"companyName\":\"X\",\"name\":\"X\",\"email\":\"not-an-email\",\"password\":\"changeme123\"}" 400
LONGPW="$(printf 'a%.0s' {1..73})"
call POST /api/auth/signup "" "{\"companyName\":\"X\",\"name\":\"X\",\"email\":\"long$RUN@example.com\",\"password\":\"$LONGPW\"}" 400
call GET /api/projects "" "" 401

echo "== Security headers"
HDRS="$(curl -s -D - -o /dev/null "$API/api/projects" -H "Authorization: Bearer $A")"
for hdr in "X-Content-Type-Options: nosniff" "X-Frame-Options: DENY" "Referrer-Policy: no-referrer"; do
  grep -qi "^$hdr" <<< "$HDRS" || { echo "FAIL: missing header $hdr"; exit 1; }
done
PASS=$((PASS + 1)); echo "ok   security headers on every response"

echo "== Clients"
call POST /api/clients "$A" '{"name":"Maria Lopez","email":"maria@example.com","phone":"619-555-0100"}' 201
CLIENT="$(field .id)"
call POST /api/clients "$A" '{"name":"   "}' 400
call POST /api/clients "$A" '{"name":"Bad Email","email":"nope"}' 400
call POST /api/clients "$A" '{"name": "Broken JSON' 400
[[ "$(field .error)" == "The request couldn't be read. Check the fields and try again." ]] || { echo "FAIL: broken JSON should get a clear message: $BODY"; exit 1; }
call PUT "/api/projects/$CLIENT/status" "$A" '{"status":"not a number"}' 400
call PUT "/api/clients/$CLIENT" "$A" '{"name":"Maria Lopez-Garcia","email":"maria@example.com","phone":""}' 200
[[ "$(field .name)" == "Maria Lopez-Garcia" && "$(field .phone)" == "null" ]] || { echo "FAIL: client edit not saved"; exit 1; }

echo "== Jobs"
call POST /api/projects "$A" "{\"name\":\"Kitchen remodel\",\"address\":\"123 Main St\",\"clientId\":\"$CLIENT\",\"startDate\":\"2026-12-01\"}" 201
JOB="$(field .id)"
call POST /api/projects "$A" '{"name":""}' 400
call POST /api/projects "$A" '{"name":"Bad dates","startDate":"2026-12-10","endDate":"2026-12-01"}' 400
call PUT "/api/projects/$JOB" "$A" "{\"name\":\"Kitchen + pantry remodel\",\"address\":\"123 Main St\",\"clientId\":\"$CLIENT\",\"startDate\":\"2026-12-01\",\"endDate\":\"2027-01-15\"}" 200
[[ "$(field .endDate)" == "2027-01-15" ]] || { echo "FAIL: job edit not saved"; exit 1; }
call PUT "/api/projects/$JOB/status" "$A" '{"status":1}' 200
call PUT "/api/projects/$JOB/status" "$A" '{"status":9}' 400

echo "== Daily logs"
call POST "/api/projects/$JOB/logs" "$A" '{"notes":"Demo started","crewCount":3,"date":"2026-12-01"}' 201
LOG="$(field .id)"
call POST "/api/projects/$JOB/logs" "$A" '{"notes":""}' 400
call PUT "/api/logs/$LOG" "$A" '{"notes":"Demo finished, dumpster swapped","crewCount":4,"date":"2026-12-01"}' 200
call GET "/api/projects/$JOB/logs" "$A" "" 200
[[ "$(field 'length')" == "1" && "$(field '.[0].crewCount')" == "4" ]] || { echo "FAIL: log list wrong"; exit 1; }

echo "== Photos on daily logs"
upload "/api/logs/$LOG/photos" "$A" 201 "$PNG" "$JPG"
[[ "$(field 'length')" == "2" && "$(field '.[0].contentType')" == "image/png" && "$(field '.[1].contentType')" == "image/jpeg" ]] || { echo "FAIL: upload response wrong"; exit 1; }
[[ "$(field '.[0].storageKey')" == "null" ]] || { echo "FAIL: storage key leaked to the browser"; exit 1; }
PHOTO="$(field '.[0].id')"
PHOTO2="$(field '.[1].id')"
upload "/api/logs/$LOG/photos" "$A" 400 "$FAKE"
call POST "/api/logs/$LOG/photos" "$A" '{"photos":[]}' 400
call GET "/api/logs/$LOG/photos" "$A" "" 200
[[ "$(field 'length')" == "2" ]] || { echo "FAIL: log photo list wrong"; exit 1; }
call GET "/api/projects/$JOB/photos" "$A" "" 200
[[ "$(field 'length')" == "2" ]] || { echo "FAIL: job photo list wrong"; exit 1; }
getfile "/api/photos/$PHOTO" "$A" 200
[[ "$CTYPE" == image/png* ]] || { echo "FAIL: photo served as $CTYPE"; exit 1; }
cmp -s /tmp/smoke_file "$PNG" || { echo "FAIL: photo bytes changed"; exit 1; }
call PUT "/api/photos/$PHOTO" "$A" '{"caption":"Kitchen after demo"}' 200
[[ "$(field .caption)" == "Kitchen after demo" ]] || { echo "FAIL: caption not saved"; exit 1; }
call DELETE "/api/photos/$PHOTO2" "$A" "" 204
getfile "/api/photos/$PHOTO2" "$A" 404

echo "== To-dos"
call POST "/api/projects/$JOB/todos" "$A" '{"title":"Order cabinets","dueDate":"2026-12-05"}' 201
TODO="$(field .id)"
call PUT "/api/todos/$TODO" "$A" '{"title":"Order cabinets and hardware","dueDate":"2026-12-06"}' 200
call POST "/api/todos/$TODO/toggle" "$A" "" 200
[[ "$(field .done)" == "true" ]] || { echo "FAIL: toggle did not mark done"; exit 1; }

echo "== Team members"
call GET /api/me "$A" "" 200
[[ "$(field .role)" == "0" && "$(field .company)" == "Alpha Builders" ]] || { echo "FAIL: signup user should be the owner"; exit 1; }
AID="$(field .id)"
call POST /api/team/invites "$A" "{\"name\":\"Olivia\",\"email\":\"office$RUN@example.com\",\"role\":1}" 201
OCODE="$(field .code)"
[[ "$(field .invite.codeHash)" == "null" ]] || { echo "FAIL: invite code hash leaked"; exit 1; }
call POST /api/team/invites "$A" "{\"name\":\"Frank\",\"email\":\"field$RUN@example.com\",\"role\":2}" 201
FCODE="$(field .code)"
call POST /api/team/invites "$A" '{"name":"Nobody","email":"nope","role":2}' 400
call POST /api/team/invites "$A" "{\"name\":\"Nobody\",\"email\":\"x$RUN@example.com\",\"role\":9}" 400
call POST /api/team/invites "$A" "{\"name\":\"Ben\",\"email\":\"bravo$RUN@example.com\",\"role\":2}" 409
call GET "/api/invites/$FCODE" "" "" 200
[[ "$(field .company)" == "Alpha Builders" && "$(field .role)" == "2" ]] || { echo "FAIL: invite lookup wrong"; exit 1; }
call GET /api/invites/not-a-real-code "" "" 404
call POST /api/auth/accept-invite "" "{\"code\":\"$FCODE\",\"password\":\"short\"}" 400
call POST /api/auth/accept-invite "" "{\"code\":\"$FCODE\",\"name\":\"Frank R\",\"password\":\"changeme123\"}" 200
F="$(field .token)"
call POST /api/auth/accept-invite "" "{\"code\":\"$FCODE\",\"password\":\"changeme123\"}" 404
call POST /api/auth/accept-invite "" "{\"code\":\"$OCODE\",\"password\":\"changeme123\"}" 200
O="$(field .token)"
call GET /api/me "$F" "" 200
[[ "$(field .role)" == "2" && "$(field .name)" == "Frank R" ]] || { echo "FAIL: field user wrong"; exit 1; }
FID="$(field .id)"
call POST /api/auth/login "" "{\"email\":\"field$RUN@example.com\",\"password\":\"changeme123\"}" 200
call GET /api/team "$A" "" 200
[[ "$(field '.members | length')" == "3" && "$(field '.invites | length')" == "0" ]] || { echo "FAIL: team list wrong"; exit 1; }

echo "== Field crew can log work but can't manage jobs"
call GET /api/projects "$F" "" 200
[[ "$(field 'length')" == "1" ]] || { echo "FAIL: field user can't see the company's jobs"; exit 1; }
call POST /api/clients "$F" '{"name":"Field client"}' 403
call PUT "/api/clients/$CLIENT" "$F" '{"name":"Changed"}' 403
call POST /api/projects "$F" '{"name":"Field job"}' 403
call PUT "/api/projects/$JOB" "$F" '{"name":"Changed"}' 403
call PUT "/api/projects/$JOB/status" "$F" '{"status":2}' 403
call DELETE "/api/projects/$JOB" "$F" "" 403
call POST /api/team/invites "$F" "{\"name\":\"X\",\"email\":\"y$RUN@example.com\",\"role\":0}" 403
call POST "/api/projects/$JOB/logs" "$F" '{"notes":"Framing walls","crewCount":2}' 201
FLOG="$(field .id)"
[[ "$(field .author)" == "Frank R" ]] || { echo "FAIL: log author wrong"; exit 1; }
call PUT "/api/logs/$FLOG" "$F" '{"notes":"Framing walls, half done","crewCount":2}' 200
call PUT "/api/logs/$LOG" "$F" '{"notes":"Changed"}' 403
call DELETE "/api/logs/$LOG" "$F" "" 403
upload "/api/logs/$FLOG/photos" "$F" 201 "$PNG"
FPHOTO="$(field '.[0].id')"
call PUT "/api/photos/$FPHOTO" "$F" '{"caption":"West wall"}' 200
call PUT "/api/photos/$PHOTO" "$F" '{"caption":"Changed"}' 403
call POST "/api/todos/$TODO/toggle" "$F" "" 200
call PUT "/api/logs/$FLOG" "$A" '{"notes":"Framing walls (checked)","crewCount":2}' 200

echo "== Office staff manage jobs but not the team"
call POST /api/clients "$O" '{"name":"Office client"}' 201
OCLIENT="$(field .id)"
call DELETE "/api/clients/$OCLIENT" "$O" "" 204
call PUT "/api/photos/$FPHOTO" "$O" '{"caption":"West wall framing"}' 200
call POST /api/team/invites "$O" "{\"name\":\"X\",\"email\":\"z$RUN@example.com\",\"role\":2}" 403
call PUT "/api/team/$FID/role" "$O" '{"role":1}' 403

echo "== Changing your password signs out other devices"
call PUT /api/me/password "$O" '{"currentPassword":"wrong-password","newPassword":"newpass456"}' 400
call PUT /api/me/password "$O" '{"currentPassword":"changeme123","newPassword":"short"}' 400
call PUT /api/me/password "$O" '{"currentPassword":"changeme123","newPassword":"newpass456"}' 200
O2="$(field .token)"
call GET /api/me "$O" "" 401
call GET /api/me "$O2" "" 200
O="$O2"
call POST /api/auth/login "" "{\"email\":\"office$RUN@example.com\",\"password\":\"changeme123\"}" 401
call POST /api/auth/login "" "{\"email\":\"office$RUN@example.com\",\"password\":\"newpass456\"}" 200

echo "== Estimates"
EST='{"title":"Kitchen remodel","markupPercent":25,"taxPercent":7.75,"notes":"Valid for 30 days.","items":[{"description":"Demo and haul away","category":0,"quantity":10,"unit":"hr","unitCost":65},{"description":"Cabinets","category":1,"quantity":3,"unit":"ea","unitCost":120}]}'
call POST "/api/projects/$JOB/estimates" "$A" "$EST" 201
ESTID="$(field .id)"
[[ "$(field '.number')" == "1" && "$(field '.status')" == "0" ]] || { echo "FAIL: new estimate should be draft #1"; exit 1; }
[[ "$(field '.items[0].unitPrice == 81.25 and .items[0].amount == 812.5 and .items[1].amount == 450')" == "true" ]] || { echo "FAIL: line prices wrong: $BODY"; exit 1; }
[[ "$(field '.subtotal == 1262.5 and .tax == 34.88 and .total == 1297.38')" == "true" ]] || { echo "FAIL: estimate totals wrong: $BODY"; exit 1; }
[[ "$(field '.cost == 1010 and .profit == 252.5 and .marginPercent == 20')" == "true" ]] || { echo "FAIL: estimate profit wrong: $BODY"; exit 1; }
call POST "/api/projects/$JOB/estimates" "$A" '{"title":"Option B","items":[{"description":"Paint","category":2,"quantity":1,"unitCost":900}]}' 201
EST2="$(field .id)"
[[ "$(field '.number')" == "2" && "$(field '.total == 900')" == "true" ]] || { echo "FAIL: second estimate wrong"; exit 1; }
call POST "/api/projects/$JOB/estimates" "$A" '{"title":"","items":[]}' 400
call POST "/api/projects/$JOB/estimates" "$A" '{"title":"No items"}' 400
call POST "/api/projects/$JOB/estimates" "$A" '{"title":"Bad line","items":[{"description":"X","category":1,"quantity":0,"unitCost":5}]}' 400
call POST "/api/projects/$JOB/estimates" "$A" '{"title":"Bad line","items":[{"description":"X","category":9,"quantity":1,"unitCost":5}]}' 400
call POST "/api/projects/$JOB/estimates" "$A" '{"title":"Bad tax","taxPercent":50,"items":[{"description":"X","category":1,"quantity":1,"unitCost":5}]}' 400
call GET "/api/projects/$JOB/estimates" "$A" "" 200
[[ "$(field 'length')" == "2" && "$(field '.[0].number')" == "2" ]] || { echo "FAIL: estimate list wrong"; exit 1; }
call PUT "/api/estimates/$ESTID" "$A" '{"title":"Kitchen remodel v1","markupPercent":20,"taxPercent":0,"items":[{"description":"Cabinets","category":1,"quantity":2,"unit":"ea","unitCost":100}]}' 200
[[ "$(field '.title')" == "Kitchen remodel v1" && "$(field '.items | length')" == "1" && "$(field '.total == 240')" == "true" ]] || { echo "FAIL: estimate edit not saved: $BODY"; exit 1; }
call GET "/api/estimates/$ESTID" "$A" "" 200
[[ "$(field '.items[0].unitPrice == 120')" == "true" ]] || { echo "FAIL: saved estimate wrong"; exit 1; }
call PUT "/api/estimates/$ESTID/status" "$A" '{"status":9}' 400
call PUT "/api/estimates/$ESTID/status" "$A" '{"status":2}' 200
call DELETE "/api/estimates/$ESTID" "$A" "" 409
call PUT "/api/estimates/$ESTID" "$A" '{"title":"Change after approval","items":[{"description":"X","category":1,"quantity":1,"unitCost":5}]}' 409
call POST "/api/estimates/$ESTID/copy" "$A" "" 201
ESTCOPY="$(field .id)"
[[ "$(field '.number')" == "3" && "$(field '.status')" == "0" && "$(field '.total == 240')" == "true" ]] || { echo "FAIL: copy wrong: $BODY"; exit 1; }
call GET "/api/projects/$JOB/estimates" "$O" "" 200
call GET "/api/projects/$JOB/estimates" "$F" "" 403
call GET "/api/estimates/$ESTID" "$F" "" 403
call POST "/api/projects/$JOB/estimates" "$F" "$EST" 403
call GET "/api/projects/$JOB/estimates" "$B" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: company B can see company A's estimates"; exit 1; }
call GET "/api/estimates/$ESTID" "$B" "" 404
call PUT "/api/estimates/$EST2" "$B" "$EST" 404
call POST "/api/estimates/$ESTID/copy" "$B" "" 404
call DELETE "/api/estimates/$EST2" "$B" "" 404
call POST "/api/projects/$JOB/estimates" "$B" "$EST" 404
call DELETE "/api/estimates/$EST2" "$A" "" 204
call GET "/api/estimates/$EST2" "$A" "" 404

echo "== Change orders"
call GET "/api/projects/$JOB/contract" "$A" "" 200
[[ "$(field '.original == 240 and .approvedChanges == 0 and .revised == 240')" == "true" ]] || { echo "FAIL: contract should start at the approved estimate: $BODY"; exit 1; }
call POST "/api/projects/$JOB/change-orders" "$A" '{"title":"Add outlets","markupPercent":20,"taxPercent":0,"daysAdded":2,"items":[{"description":"Outlets","category":0,"quantity":4,"unit":"ea","unitCost":50}]}' 201
CO1="$(field .id)"
[[ "$(field '.number')" == "1" && "$(field '.kind')" == "1" && "$(field '.daysAdded')" == "2" && "$(field '.total == 240')" == "true" ]] || { echo "FAIL: change order wrong: $BODY"; exit 1; }
call POST "/api/projects/$JOB/change-orders" "$A" '{"title":"Remove backsplash","items":[{"description":"Backsplash credit","category":1,"quantity":1,"unitCost":-100}]}' 201
CO2="$(field .id)"
[[ "$(field '.number')" == "2" && "$(field '.total == -100')" == "true" ]] || { echo "FAIL: credit change order wrong: $BODY"; exit 1; }
call POST "/api/projects/$JOB/estimates" "$A" '{"title":"No credits","items":[{"description":"X","category":1,"quantity":1,"unitCost":-5}]}' 400
call POST "/api/projects/$JOB/change-orders" "$A" '{"title":"Too long","daysAdded":9999,"items":[{"description":"X","category":0,"quantity":1,"unitCost":5}]}' 400
call GET "/api/projects/$JOB/change-orders" "$A" "" 200
[[ "$(field 'length')" == "2" ]] || { echo "FAIL: change order list wrong"; exit 1; }
call GET "/api/projects/$JOB/estimates" "$A" "" 200
[[ "$(field 'length')" == "2" ]] || { echo "FAIL: change orders showed up in the estimate list"; exit 1; }
call GET "/api/projects/$JOB/contract" "$A" "" 200
[[ "$(field '.pendingChanges == 140 and .pendingCount == 2 and .revised == 240')" == "true" ]] || { echo "FAIL: pending changes wrong: $BODY"; exit 1; }
call PUT "/api/estimates/$CO1/status" "$A" '{"status":2}' 200
call PUT "/api/estimates/$CO1" "$A" '{"title":"Change after approval","items":[{"description":"X","category":0,"quantity":1,"unitCost":5}]}' 409
call GET "/api/projects/$JOB/contract" "$A" "" 200
[[ "$(field '.approvedChanges == 240 and .revised == 480 and .pendingChanges == -100 and .daysAdded == 2')" == "true" ]] || { echo "FAIL: contract after approval wrong: $BODY"; exit 1; }
call PUT "/api/estimates/$CO2" "$A" '{"title":"Remove backsplash","daysAdded":-1,"items":[{"description":"Backsplash credit","category":1,"quantity":1,"unitCost":-150}]}' 200
[[ "$(field '.total == -150' )" == "true" && "$(field '.daysAdded')" == "-1" ]] || { echo "FAIL: change order edit wrong: $BODY"; exit 1; }
call POST "/api/estimates/$CO2/copy" "$A" "" 201
[[ "$(field '.kind')" == "1" && "$(field '.number')" == "3" ]] || { echo "FAIL: change order copy wrong: $BODY"; exit 1; }
call GET "/api/projects/$JOB/change-orders" "$F" "" 403
call GET "/api/projects/$JOB/contract" "$F" "" 403
call POST "/api/projects/$JOB/change-orders" "$F" '{"title":"X","items":[{"description":"X","category":0,"quantity":1,"unitCost":5}]}' 403
call GET "/api/projects/$JOB/change-orders" "$O" "" 200
call GET "/api/projects/$JOB/contract" "$B" "" 404
call GET "/api/projects/$JOB/change-orders" "$B" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: company B can see company A's change orders"; exit 1; }
call POST "/api/projects/$JOB/change-orders" "$B" '{"title":"X","items":[{"description":"X","category":0,"quantity":1,"unitCost":5}]}' 404
call GET "/api/estimates/$CO1" "$B" "" 404

echo "== Invoices and payments"
DAY0="$(date -u +%F)"
DUE="$(date -u -d '+14 days' +%F)"
PAST_ISSUE="$(date -u -d '-20 days' +%F)"
PAST_DUE="$(date -u -d '-5 days' +%F)"
call GET "/api/projects/$JOB/billing" "$A" "" 200
[[ "$(field '.contract == 480 and .invoiced == 0 and .paid == 0 and .leftToBill == 480')" == "true" ]] || { echo "FAIL: billing should start at the revised contract: $BODY"; exit 1; }
call POST "/api/projects/$JOB/invoices" "$A" "{\"title\":\"Deposit\",\"issueDate\":\"$DAY0\",\"dueDate\":\"$DUE\",\"lines\":[{\"description\":\"Deposit 30% of contract\",\"amount\":144}]}" 201
INV1="$(field .id)"
[[ "$(field '.number')" == "1" && "$(field '.state')" == "draft" && "$(field '.total == 144 and .balance == 144')" == "true" ]] || { echo "FAIL: new invoice wrong: $BODY"; exit 1; }
call POST "/api/projects/$JOB/invoices" "$A" '{"title":"No lines","lines":[]}' 400
call POST "/api/projects/$JOB/invoices" "$A" '{"title":"Zero","lines":[{"description":"Credit","amount":-5}]}' 400
call POST "/api/projects/$JOB/invoices" "$A" '{"title":"Dates","issueDate":"2026-12-10","dueDate":"2026-12-01","lines":[{"description":"X","amount":5}]}' 400
call GET "/api/projects/$JOB/billing" "$A" "" 200
[[ "$(field '.invoiced == 0 and .draftCount == 1')" == "true" ]] || { echo "FAIL: drafts shouldn't count as invoiced: $BODY"; exit 1; }
call PUT "/api/invoices/$INV1" "$A" "{\"title\":\"Deposit\",\"issueDate\":\"$DAY0\",\"dueDate\":\"$DUE\",\"notes\":\"Thank you!\",\"lines\":[{\"description\":\"Deposit 30% of contract\",\"amount\":144},{\"description\":\"Permit fee\",\"amount\":56}]}" 200
[[ "$(field '.total == 200')" == "true" ]] || { echo "FAIL: invoice edit not saved: $BODY"; exit 1; }
call PUT "/api/invoices/$INV1/status" "$A" '{"status":1}' 200
[[ "$(field '.state')" == "sent" ]] || { echo "FAIL: invoice should be sent: $BODY"; exit 1; }
call POST "/api/invoices/$INV1/payments" "$A" "{\"amount\":150,\"date\":\"$DAY0\",\"method\":0,\"reference\":\"Check 1042\"}" 201
PAY1="$(field '.payments[0].id')"
[[ "$(field '.state')" == "partial" && "$(field '.paid == 150 and .balance == 50')" == "true" ]] || { echo "FAIL: partial payment wrong: $BODY"; exit 1; }
call POST "/api/invoices/$INV1/payments" "$A" '{"amount":60,"method":3}' 400
call POST "/api/invoices/$INV1/payments" "$A" '{"amount":0,"method":3}' 400
call POST "/api/invoices/$INV1/payments" "$A" '{"amount":10,"method":9}' 400
call PUT "/api/invoices/$INV1" "$A" '{"title":"Change","lines":[{"description":"X","amount":5}]}' 409
call PUT "/api/invoices/$INV1/status" "$A" '{"status":2}' 409
call DELETE "/api/invoices/$INV1" "$A" "" 409
call GET "/api/projects/$JOB/billing" "$A" "" 200
[[ "$(field '.invoiced == 200 and .paid == 150 and .balanceDue == 50 and .leftToBill == 280')" == "true" ]] || { echo "FAIL: billing after payment wrong: $BODY"; exit 1; }
call POST "/api/invoices/$INV1/payments" "$A" '{"amount":50,"method":3}' 201
[[ "$(field '.state')" == "paid" && "$(field '.balance == 0')" == "true" && "$(field '.payments | length')" == "2" ]] || { echo "FAIL: invoice should be paid: $BODY"; exit 1; }
call DELETE "/api/payments/$PAY1" "$A" "" 204
call GET "/api/invoices/$INV1" "$A" "" 200
[[ "$(field '.state')" == "partial" && "$(field '.paid == 50')" == "true" ]] || { echo "FAIL: deleting a payment didn't reopen the balance: $BODY"; exit 1; }
call POST "/api/projects/$JOB/invoices" "$A" "{\"title\":\"Progress 1\",\"issueDate\":\"$PAST_ISSUE\",\"dueDate\":\"$PAST_DUE\",\"lines\":[{\"description\":\"Rough-in\",\"amount\":100}]}" 201
INV2="$(field .id)"
[[ "$(field '.number')" == "2" ]] || { echo "FAIL: second invoice number wrong"; exit 1; }
call PUT "/api/invoices/$INV2/status" "$A" '{"status":1}' 200
[[ "$(field '.state')" == "overdue" ]] || { echo "FAIL: past-due invoice should be overdue: $BODY"; exit 1; }
call DELETE "/api/invoices/$INV2" "$A" "" 409
call GET "/api/projects/$JOB/billing" "$A" "" 200
[[ "$(field '.overdueCount == 1 and .invoiced == 300')" == "true" ]] || { echo "FAIL: overdue count wrong: $BODY"; exit 1; }
call PUT "/api/invoices/$INV2/status" "$A" '{"status":2}' 200
[[ "$(field '.state')" == "void" ]] || { echo "FAIL: invoice should be void: $BODY"; exit 1; }
call POST "/api/invoices/$INV2/payments" "$A" '{"amount":10,"method":1}' 409
call PUT "/api/invoices/$INV2" "$A" '{"title":"Change","lines":[{"description":"X","amount":5}]}' 409
call GET "/api/projects/$JOB/billing" "$A" "" 200
[[ "$(field '.invoiced == 200 and .overdueCount == 0')" == "true" ]] || { echo "FAIL: void invoice still counted: $BODY"; exit 1; }
call DELETE "/api/invoices/$INV2" "$A" "" 204
call GET "/api/projects/$JOB/invoices" "$A" "" 200
[[ "$(field 'length')" == "1" ]] || { echo "FAIL: invoice list wrong"; exit 1; }
call GET "/api/projects/$JOB/invoices" "$O" "" 200
call GET "/api/projects/$JOB/invoices" "$F" "" 403
call GET "/api/projects/$JOB/billing" "$F" "" 403
call POST "/api/invoices/$INV1/payments" "$F" '{"amount":1,"method":2}' 403
call GET "/api/projects/$JOB/billing" "$B" "" 404
call GET "/api/projects/$JOB/invoices" "$B" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: company B can see company A's invoices"; exit 1; }
call GET "/api/invoices/$INV1" "$B" "" 404
call POST "/api/invoices/$INV1/payments" "$B" '{"amount":1,"method":2}' 404
call POST "/api/projects/$JOB/invoices" "$B" '{"title":"X","lines":[{"description":"X","amount":5}]}' 404
call GET "/api/invoices/$INV1" "$A" "" 200
PAY2="$(field '.payments[0].id')"
call DELETE "/api/payments/$PAY2" "$B" "" 404

echo "== Password reset links"
call POST "/api/team/$FID/reset-link" "$F" "" 403
call POST "/api/team/$FID/reset-link" "$O" "" 403
call POST "/api/team/$FID/reset-link" "$B" "" 404
call POST "/api/team/$AID/reset-link" "$A" "" 400
call POST "/api/team/$FID/reset-link" "$A" "" 200
RCODE="$(field .code)"
[[ "$(field .email)" == "field$RUN@example.com" ]] || { echo "FAIL: reset link for the wrong person: $BODY"; exit 1; }
call GET "/api/password-resets/$RCODE" "" "" 200
[[ "$(field .name)" == "Frank R" && "$(field .company)" == "Alpha Builders" ]] || { echo "FAIL: reset lookup wrong: $BODY"; exit 1; }
call GET /api/password-resets/not-a-real-code "" "" 404
[[ "${RCODE:40:1}" == "A" ]] && SWAP=B || SWAP=A
call GET "/api/password-resets/${RCODE:0:40}$SWAP${RCODE:41}" "" "" 404
call POST /api/auth/reset-password "" "{\"code\":\"$RCODE\",\"password\":\"short\"}" 400
call POST /api/auth/reset-password "" "{\"code\":\"$RCODE\",\"password\":\"resetpass789\"}" 200
F2="$(field .token)"
call GET /api/me "$F" "" 401
call GET /api/me "$F2" "" 200
F="$F2"
call POST /api/auth/reset-password "" "{\"code\":\"$RCODE\",\"password\":\"another123\"}" 404
call POST /api/auth/login "" "{\"email\":\"field$RUN@example.com\",\"password\":\"resetpass789\"}" 200

echo "== Owner manages roles"
call GET /api/team "$B" "" 200
[[ "$(field '.members | length')" == "1" ]] || { echo "FAIL: company B can see company A's team"; exit 1; }
call PUT "/api/team/$FID/role" "$B" '{"role":0}' 404
call DELETE "/api/team/$FID" "$B" "" 404
call PUT "/api/team/$FID/role" "$A" '{"role":1}' 200
call POST /api/clients "$F" '{"name":"Now allowed"}' 201
call DELETE "/api/clients/$(field .id)" "$F" "" 204
call PUT "/api/team/$FID/role" "$A" '{"role":2}' 200
call POST /api/clients "$F" '{"name":"Blocked again"}' 403
call PUT "/api/team/$AID/role" "$A" '{"role":1}' 400
call DELETE "/api/team/$AID" "$A" "" 400
call PUT "/api/team/$AID/role" "$A" '{"role":9}' 400
call POST /api/team/invites "$A" "{\"name\":\"Temp\",\"email\":\"temp$RUN@example.com\",\"role\":2}" 201
TCODE="$(field .code)"
call POST /api/team/invites "$A" "{\"name\":\"Temp\",\"email\":\"temp$RUN@example.com\",\"role\":1}" 201
OLDCODE="$TCODE"
TCODE="$(field .code)"
call GET "/api/invites/$OLDCODE" "" "" 404
call GET /api/team "$A" "" 200
[[ "$(field '.invites | length')" == "1" ]] || { echo "FAIL: re-invite should replace the old invite"; exit 1; }
TINV="$(field '.invites[0].id')"
call DELETE "/api/team/invites/$TINV" "$A" "" 204
call POST /api/auth/accept-invite "" "{\"code\":\"$TCODE\",\"password\":\"changeme123\"}" 404
call DELETE "/api/team/$FID" "$A" "" 204
call GET /api/projects "$F" "" 401
call POST /api/auth/login "" "{\"email\":\"field$RUN@example.com\",\"password\":\"changeme123\"}" 401
call GET "/api/projects/$JOB/logs" "$A" "" 200
[[ "$(field 'length')" == "2" ]] || { echo "FAIL: removed member's log should stay"; exit 1; }

echo "== Company B can't see or change company A's data"
call GET /api/projects "$B" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: company B can see company A's jobs"; exit 1; }
call GET /api/clients "$B" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: company B can see company A's clients"; exit 1; }
call GET "/api/projects/$JOB" "$B" "" 404
call PUT "/api/projects/$JOB" "$B" '{"name":"Hacked"}' 404
call PUT "/api/clients/$CLIENT" "$B" '{"name":"Hacked"}' 404
call PUT "/api/logs/$LOG" "$B" '{"notes":"Hacked"}' 404
call PUT "/api/todos/$TODO" "$B" '{"title":"Hacked"}' 404
call POST "/api/projects/$JOB/logs" "$B" '{"notes":"Hacked"}' 404
call POST "/api/projects" "$B" "{\"name\":\"Steal client\",\"clientId\":\"$CLIENT\"}" 400
call DELETE "/api/projects/$JOB" "$B" "" 404
call DELETE "/api/clients/$CLIENT" "$B" "" 404
call DELETE "/api/logs/$LOG" "$B" "" 404
call DELETE "/api/todos/$TODO" "$B" "" 404
getfile "/api/photos/$PHOTO" "$B" 404
call GET "/api/projects/$JOB/photos" "$B" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: company B can see company A's photos"; exit 1; }
call GET "/api/logs/$LOG/photos" "$B" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: company B can see company A's log photos"; exit 1; }
upload "/api/logs/$LOG/photos" "$B" 404 "$PNG"
call PUT "/api/photos/$PHOTO" "$B" '{"caption":"Hacked"}' 404
call DELETE "/api/photos/$PHOTO" "$B" "" 404

echo "== Too many wrong passwords lock the email for a while"
for n in 1 2 3 4 5; do
  call POST /api/auth/login "" "{\"email\":\"bravo$RUN@example.com\",\"password\":\"wrong-password\"}" 401
done
call POST /api/auth/login "" "{\"email\":\"bravo$RUN@example.com\",\"password\":\"changeme123\"}" 429
[[ "$(field .error)" == "Too many wrong passwords"* ]] || { echo "FAIL: lockout message wrong: $BODY"; exit 1; }
call GET /api/me "$B" "" 200

echo "== Two people creating at the same moment get different numbers"
call POST /api/projects "$B" '{"name":"Bravo job"}' 201
BJOB="$(field .id)"
: > /tmp/smoke_parallel
for n in 1 2 3 4 5 6; do
  curl -s -o /dev/null -w "%{http_code}\n" -X POST "$API/api/projects/$BJOB/invoices" -H "Content-Type: application/json" -H "Authorization: Bearer $B" \
    -d '{"title":"Same time","lines":[{"description":"X","amount":10}]}' >> /tmp/smoke_parallel &
done
wait
[[ "$(grep -c '^201$' /tmp/smoke_parallel)" == "6" ]] || { echo "FAIL: parallel invoices didn't all save: $(tr '\n' ' ' < /tmp/smoke_parallel)"; exit 1; }
PASS=$((PASS + 1)); echo "ok   6 invoices created at once -> all 201"
call GET "/api/projects/$BJOB/invoices" "$B" "" 200
[[ "$(field '[.[].number] | unique | length')" == "6" && "$(field '[.[].number] | max')" == "6" ]] || { echo "FAIL: parallel invoices got duplicate numbers: $BODY"; exit 1; }

echo "== Deleting"
call DELETE "/api/todos/$TODO" "$A" "" 204
call DELETE "/api/logs/$LOG" "$A" "" 204
getfile "/api/photos/$PHOTO" "$A" 404
call DELETE "/api/clients/$CLIENT" "$A" "" 204
call GET "/api/projects/$JOB" "$A" "" 200
[[ "$(field .clientId)" == "null" ]] || { echo "FAIL: job still points at deleted client"; exit 1; }
call POST "/api/projects/$JOB/logs" "$A" '{"notes":"Second log"}' 201
LOG2="$(field .id)"
upload "/api/logs/$LOG2/photos" "$A" 201 "$PNG"
PHOTO3="$(field '.[0].id')"
call POST "/api/projects/$JOB/todos" "$A" '{"title":"Second to-do"}' 201
call DELETE "/api/projects/$JOB" "$A" "" 409
call DELETE "/api/payments/$PAY2" "$A" "" 204
call PUT "/api/invoices/$INV1/status" "$A" '{"status":0}' 200
call PUT "/api/estimates/$ESTID/status" "$A" '{"status":0}' 200
call DELETE "/api/projects/$JOB" "$A" "" 409
call PUT "/api/estimates/$CO1/status" "$A" '{"status":0}' 200
call DELETE "/api/projects/$JOB" "$A" "" 204
call GET "/api/projects/$JOB/logs" "$A" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: logs left behind after job delete"; exit 1; }
call GET /api/todos "$A" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: to-dos left behind after job delete"; exit 1; }
getfile "/api/photos/$PHOTO3" "$A" 404
call GET "/api/estimates/$ESTCOPY" "$A" "" 404
call GET "/api/estimates/$CO1" "$A" "" 404
call GET "/api/invoices/$INV1" "$A" "" 404

# Last, because it uses up this network's login tries for a minute.
echo "== Too many tries from one network get slowed down"
LIMITED=""
for n in $(seq 1 130); do
  if [[ "$(curl -s -o /tmp/smoke_body -w "%{http_code}" "$API/api/invites/not-a-real-code")" == "429" ]]; then LIMITED=$n; break; fi
done
[[ -n "$LIMITED" ]] || { echo "FAIL: 130 tries in a row were never slowed down"; exit 1; }
BODY="$(cat /tmp/smoke_body)"
[[ "$(field .error)" == "Too many tries. Wait a minute and try again." ]] || { echo "FAIL: rate limit message wrong: $BODY"; exit 1; }
PASS=$((PASS + 1)); echo "ok   slowed down after $LIMITED quick tries -> 429"

echo
echo "All $PASS checks passed."

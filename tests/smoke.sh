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
[[ "$(curl -s -o /dev/null -w "%{http_code}" "$API/time.js")" == "200" ]] || { echo "FAIL: time.js isn't served"; exit 1; }
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
[[ "$(field .number)" == "1001" && "$(field '.managerIds | length')" == "0" ]] || { echo "FAIL: first job should be number 1001 with no managers: $BODY"; exit 1; }
call POST /api/projects "$A" '{"name":"Same number","number":1001}' 409
call POST /api/projects "$A" '{"name":"Bad number","number":0}' 400
call POST /api/projects "$A" '{"name":""}' 400
call POST /api/projects "$A" '{"name":"Bad dates","startDate":"2026-12-10","endDate":"2026-12-01"}' 400
call PUT "/api/projects/$JOB" "$A" "{\"name\":\"Kitchen + pantry remodel\",\"address\":\"123 Main St\",\"clientId\":\"$CLIENT\",\"startDate\":\"2026-12-01\",\"endDate\":\"2027-01-15\"}" 200
[[ "$(field .number)" == "1001" ]] || { echo "FAIL: editing a job without a number should keep its number: $BODY"; exit 1; }
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

echo "== Job numbers and project managers"
call GET /api/me "$B" "" 200
BID="$(field .id)"
JOBEDIT="{\"name\":\"Kitchen + pantry remodel\",\"address\":\"123 Main St\",\"clientId\":\"$CLIENT\",\"startDate\":\"2026-12-01\",\"endDate\":\"2027-01-15\""
call PUT "/api/projects/$JOB" "$A" "$JOBEDIT,\"number\":5225,\"managerIds\":[\"$AID\",\"$FID\",\"$AID\"]}" 200
[[ "$(field .number)" == "5225" && "$(echo "$BODY" | jq -r --arg a "$AID" --arg f "$FID" '.managerIds | sort == ([$a, $f] | sort)')" == "true" ]] || { echo "FAIL: job number or managers not saved (duplicates should be dropped): $BODY"; exit 1; }
call PUT "/api/projects/$JOB" "$A" "$JOBEDIT,\"managerIds\":[\"$BID\"]}" 400
call PUT "/api/projects/$JOB" "$A" "$JOBEDIT,\"number\":10000000}" 400
call GET "/api/projects/$JOB" "$A" "" 200
[[ "$(field .number)" == "5225" && "$(field '.managerIds | length')" == "2" ]] || { echo "FAIL: a refused edit changed the job: $BODY"; exit 1; }
call PUT "/api/projects/$JOB" "$O" "$JOBEDIT,\"number\":5225}" 200

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

echo "== Recent activity"
call GET /api/activity "$A" "" 200
[[ "$(field 'length')" == "30" ]] || { echo "FAIL: activity should return 30 lines by default: $(field length)"; exit 1; }
[[ "$(field '.[0].text')" == "deleted invoice #2 · Progress 1" && "$(field '.[0].userName')" == "Ana" && "$(field '.[0].jobName')" == "#5225 Kitchen + pantry remodel" ]] || { echo "FAIL: newest activity line wrong: $(field '.[0]')"; exit 1; }
OLDEST="$(field '.[29].createdAt')"
FIRSTPAGE="$(field 'map(.id)')"
call GET "/api/activity?before=$OLDEST&limit=100" "$A" "" 200
[[ "$(echo "$BODY" | jq -r --argjson first "$FIRSTPAGE" 'length > 0 and all(.[]; .id as $i | $first | index($i) == null)')" == "true" ]] || { echo "FAIL: the older page repeats lines from the first page: $BODY"; exit 1; }
[[ "$(field 'map(.text) | index("created the job") != null')" == "true" ]] || { echo "FAIL: job creation missing from activity"; exit 1; }
call GET "/api/activity?limit=100" "$A" "" 200
for t in "added client Maria Lopez" "changed the status to Active" "added 2 photos to the daily log for Tue, Dec 1" "checked off a to-do · Order cabinets and hardware" \
         "invited Frank to join as Field" "joined the team" "changed the job number from 1001 to 5225" "created estimate #1 · Kitchen remodel" \
         "marked estimate #1 approved" "copied estimate #1 to #3" "marked change order #1 approved" "recorded a \$150.00 payment on invoice #1"; do
  [[ "$(echo "$BODY" | jq -r --arg t "$t" 'map(.text) | index($t) != null')" == "true" ]] || { echo "FAIL: activity is missing: $t"; exit 1; }
done
PASS=$((PASS + 1)); echo "ok   activity lists jobs, logs, photos, to-dos, team, estimates and payments"
call GET "/api/activity?projectId=$JOB&limit=100" "$A" "" 200
[[ "$(echo "$BODY" | jq -r --arg j "$JOB" 'length > 0 and all(.[]; .projectId == $j)')" == "true" ]] || { echo "FAIL: job activity includes other jobs: $BODY"; exit 1; }
call GET "/api/activity?userId=$FID&limit=100" "$A" "" 200
[[ "$(echo "$BODY" | jq -r 'length > 0 and all(.[]; .userName == "Frank R")')" == "true" ]] || { echo "FAIL: activity by person wrong: $BODY"; exit 1; }
call GET "/api/activity?limit=100" "$F" "" 200
[[ "$(field 'length > 0 and all(.[]; .officeOnly == false)')" == "true" ]] || { echo "FAIL: field crew can see estimate or invoice activity"; exit 1; }
call GET /api/activity "$B" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: company B can see company A's activity: $BODY"; exit 1; }
call GET "/api/activity?before=not-a-date" "$A" "" 400
call GET /api/activity "" "" 401

echo "== Schedule"
call POST "/api/projects/$JOB/schedule" "$A" "{\"title\":\"Framing\",\"startDate\":\"2026-12-07\",\"endDate\":\"2026-12-11\",\"assigneeIds\":[\"$FID\",\"$FID\"],\"notes\":\"Walls and roof\"}" 201
SCH="$(field .id)"
[[ "$(field '.assigneeIds | length')" == "1" && "$(field .done)" == "false" && "$(field .notes)" == "Walls and roof" ]] || { echo "FAIL: schedule item wrong: $BODY"; exit 1; }
call POST "/api/projects/$JOB/schedule" "$O" '{"title":"Rough inspection","startDate":"2026-12-14"}' 201
SCH2="$(field .id)"
[[ "$(field .endDate)" == "2026-12-14" ]] || { echo "FAIL: a one-day item should end the day it starts: $BODY"; exit 1; }
call POST "/api/projects/$JOB/schedule" "$A" '{"title":"","startDate":"2026-12-14"}' 400
call POST "/api/projects/$JOB/schedule" "$A" '{"title":"No date"}' 400
call POST "/api/projects/$JOB/schedule" "$A" '{"title":"Backwards","startDate":"2026-12-14","endDate":"2026-12-10"}' 400
call POST "/api/projects/$JOB/schedule" "$A" "{\"title\":\"Wrong team\",\"startDate\":\"2026-12-14\",\"assigneeIds\":[\"$BID\"]}" 400
call POST "/api/projects/$JOB/schedule" "$F" '{"title":"Field plan","startDate":"2026-12-14"}' 403
call GET "/api/projects/$JOB/schedule" "$F" "" 200
[[ "$(field 'length')" == "2" && "$(field '.[0].title')" == "Framing" ]] || { echo "FAIL: job schedule wrong: $BODY"; exit 1; }
call PUT "/api/schedule/$SCH" "$A" "{\"title\":\"Framing\",\"startDate\":\"2026-12-08\",\"endDate\":\"2026-12-12\",\"assigneeIds\":[\"$FID\"]}" 200
[[ "$(field .startDate)" == "2026-12-08" && "$(field .notes)" == "null" ]] || { echo "FAIL: schedule edit not saved: $BODY"; exit 1; }
call PUT "/api/schedule/$SCH" "$F" '{"title":"Changed","startDate":"2026-12-08"}' 403
call DELETE "/api/schedule/$SCH" "$F" "" 403
call POST "/api/schedule/$SCH/toggle" "$F" "" 200
[[ "$(field .done)" == "true" ]] || { echo "FAIL: field crew should be able to check off schedule items: $BODY"; exit 1; }
call GET "/api/schedule?from=2026-12-07&to=2026-12-13" "$A" "" 200
[[ "$(field '.items | length')" == "1" && "$(field '.items[0].title')" == "Framing" && "$(field '.workDays == [1,2,3,4,5]')" == "true" ]] || { echo "FAIL: agenda wrong: $BODY"; exit 1; }
call GET "/api/schedule?from=2026-12-07&to=2026-12-20&userId=$FID" "$A" "" 200
[[ "$(field '.items | length')" == "1" ]] || { echo "FAIL: agenda for one person wrong: $BODY"; exit 1; }
call GET "/api/schedule?from=2026-12-07&to=2026-12-20" "$F" "" 200
[[ "$(field '.items | length')" == "2" ]] || { echo "FAIL: field crew should see the whole schedule: $BODY"; exit 1; }
call GET /api/schedule "$A" "" 200
[[ "$(date -d "$(field .from)" +%u)" == "1" && "$(date -d "$(field .to)" +%u)" == "7" ]] || { echo "FAIL: the default agenda should be Monday to Sunday: $BODY"; exit 1; }
call GET "/api/schedule?from=2026-12-07&to=2027-03-01" "$A" "" 400
call GET "/api/schedule?from=2026-12-14&to=2026-12-07" "$A" "" 400
call GET "/api/schedule?from=2026-12-07&to=2026-12-20" "$B" "" 200
[[ "$(field '.items | length')" == "0" ]] || { echo "FAIL: company B can see company A's schedule"; exit 1; }
call GET "/api/projects/$JOB/schedule" "$B" "" 404
call POST "/api/projects/$JOB/schedule" "$B" '{"title":"X","startDate":"2026-12-14"}' 404
call PUT "/api/schedule/$SCH" "$B" '{"title":"Hacked","startDate":"2026-12-08"}' 404
call POST "/api/schedule/$SCH/toggle" "$B" "" 404
call DELETE "/api/schedule/$SCH" "$B" "" 404

echo "== Work calendar"
call GET /api/work-calendar "$F" "" 200
[[ "$(field '.workDays == [1,2,3,4,5] and (.nonWorkdays | length) == 0')" == "true" ]] || { echo "FAIL: new companies should work Monday to Friday: $BODY"; exit 1; }
call PUT /api/work-calendar "$O" '{"workDays":[6,1,2,3,4,5],"nonWorkdays":[{"date":"2026-12-25","name":"Christmas"},{"date":"2027-01-01"}]}' 200
[[ "$(field '.workDays == [1,2,3,4,5,6] and (.nonWorkdays | length) == 2')" == "true" && "$(field '.nonWorkdays[1].name')" == "Day off" ]] || { echo "FAIL: work calendar not saved: $BODY"; exit 1; }
call PUT /api/work-calendar "$A" '{"workDays":[]}' 400
call PUT /api/work-calendar "$A" '{"workDays":[7]}' 400
call PUT /api/work-calendar "$A" '{"workDays":[1],"nonWorkdays":[{"date":"2026-12-25"},{"date":"2026-12-25"}]}' 400
call PUT /api/work-calendar "$F" '{"workDays":[1,2,3]}' 403
call GET "/api/schedule?from=2026-12-21&to=2026-12-27" "$F" "" 200
[[ "$(field '.nonWorkdays | length')" == "1" && "$(field '.nonWorkdays[0].name')" == "Christmas" && "$(field '.workDays | index(6) != null')" == "true" ]] || { echo "FAIL: agenda should show days off: $BODY"; exit 1; }
call GET /api/work-calendar "$B" "" 200
[[ "$(field '.workDays == [1,2,3,4,5] and (.nonWorkdays | length) == 0')" == "true" ]] || { echo "FAIL: company B sees company A's work calendar: $BODY"; exit 1; }
call GET "/api/activity?limit=20" "$A" "" 200
for t in "scheduled Framing for Mon, Dec 7 to Fri, Dec 11" "moved Framing to Tue, Dec 8 to Sat, Dec 12" "finished Framing" "updated the work calendar"; do
  [[ "$(echo "$BODY" | jq -r --arg t "$t" 'map(.text) | index($t) != null')" == "true" ]] || { echo "FAIL: activity is missing: $t"; exit 1; }
done
PASS=$((PASS + 1)); echo "ok   schedule changes show in the activity"

echo "== Time clock"
call GET /api/time/current "$F" "" 200
[[ "$(field .entry)" == "null" ]] || { echo "FAIL: nobody should be on the clock yet: $BODY"; exit 1; }
call POST /api/time/clock-in "$F" '{}' 400
call POST /api/time/clock-in "$F" '{"projectId":"00000000-0000-0000-0000-000000000001"}' 400
call POST /api/time/clock-in "$F" "{\"projectId\":\"$JOB\",\"notes\":\"Framing\"}" 200
FT1="$(field .id)"
[[ "$(field .jobName)" == "#5225 Kitchen + pantry remodel" && "$(field .userName)" == "Frank R" && "$(field .clockOut)" == "null" && "$(field .minutes)" == "null" ]] || { echo "FAIL: clock-in wrong: $BODY"; exit 1; }
call POST /api/time/clock-in "$F" "{\"projectId\":\"$JOB\"}" 409
call GET /api/time/current "$F" "" 200
[[ "$(field .entry.id)" == "$FT1" ]] || { echo "FAIL: current entry wrong: $BODY"; exit 1; }
call GET /api/time/now "$O" "" 200
[[ "$(field 'length')" == "1" && "$(field '.[0].userName')" == "Frank R" ]] || { echo "FAIL: who's on the clock wrong: $BODY"; exit 1; }
call GET /api/time/now "$F" "" 403
call POST /api/projects "$A" '{"name":"Garage ADU"}' 201
JOB2="$(field .id)"
call POST /api/time/switch "$F" "{\"projectId\":\"$JOB\"}" 400
call POST /api/time/switch "$F" "{\"projectId\":\"$JOB2\"}" 200
FT2="$(field .id)"
[[ "$(field .jobName)" == "#5226 Garage ADU" && "$(field .clockOut)" == "null" ]] || { echo "FAIL: switching jobs wrong: $BODY"; exit 1; }
call GET /api/time "$F" "" 200
[[ "$(echo "$BODY" | jq -r --arg a "$FT1" --arg b "$FT2" '([.entries[] | select(.id == $a and .clockOut != null)] | length == 1) and ([.entries[] | select(.id == $b and .clockOut == null)] | length == 1) and .people[0].onTheClock')" == "true" ]] || { echo "FAIL: switching should close the first job's time: $BODY"; exit 1; }
call POST /api/time/clock-out "$F" '{"breakMinutes":-5}' 400
call POST /api/time/clock-out "$F" '{"breakMinutes":600}' 400
call POST /api/time/clock-out "$F" '{"notes":"Done for the day"}' 200
[[ "$(field .clockOut)" != "null" && "$(field .minutes)" == "0" && "$(field .notes)" == "Done for the day" ]] || { echo "FAIL: clock-out wrong: $BODY"; exit 1; }
call POST /api/time/clock-out "$F" '{}' 409
: > /tmp/smoke_parallel
for n in 1 2 3 4; do
  curl -s -o /dev/null -w "%{http_code}\n" -X POST "$API/api/time/clock-in" -H "Content-Type: application/json" -H "Authorization: Bearer $O" \
    -d "{\"projectId\":\"$JOB\"}" >> /tmp/smoke_parallel &
done
wait
[[ "$(grep -c '^200$' /tmp/smoke_parallel)" == "1" && "$(grep -c '^409$' /tmp/smoke_parallel)" == "3" ]] || { echo "FAIL: four quick clock-ins should give one entry: $(tr '\n' ' ' < /tmp/smoke_parallel)"; exit 1; }
PASS=$((PASS + 1)); echo "ok   4 clock-ins at once -> one on the clock"
call POST /api/time/clock-out "$O" '{}' 200

# A week of hand-entered time (Pacific, no time zone) for Frank, California overtime rules:
# Mon 10 h (2 OT), Tue 13 h (4 OT, 1 double), Wed-Fri 8 h, Sat 4 h (past 40 regular, so OT).
add_time() { call POST /api/time "$O" "{\"userId\":\"$FID\",\"projectId\":\"$JOB\",\"clockIn\":\"$1\",\"clockOut\":\"$2\",\"breakMinutes\":$3$4}" 201; }
add_time 2026-11-02T07:00:00 2026-11-02T17:30:00 30 ',"notes":"=SUM(A1)"'
TMON="$(field .id)"
[[ "$(field .minutes)" == "600" && "$(field .day)" == "2026-11-02" && "$(field .editedBy)" == "Olivia" && "$(field .clockIn)" == "2026-11-02T15:00:00Z" ]] || { echo "FAIL: hand-entered time wrong: $BODY"; exit 1; }
add_time 2026-11-03T06:00:00 2026-11-03T19:00:00 0
add_time 2026-11-04T07:00:00 2026-11-04T15:30:00 30
add_time 2026-11-05T07:00:00 2026-11-05T15:30:00 30
add_time 2026-11-06T07:00:00 2026-11-06T15:30:00 30
add_time 2026-11-07T15:00:00Z 2026-11-07T19:00:00Z 0
TSAT="$(field .id)"
[[ "$(field .day)" == "2026-11-07" && "$(field .minutes)" == "240" ]] || { echo "FAIL: time sent in UTC should land on the Pacific day: $BODY"; exit 1; }
call POST /api/time "$A" "{\"userId\":\"$AID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-02T08:00:00\",\"clockOut\":\"2026-11-02T12:00:00\"}" 201
call POST /api/time "$O" "{\"userId\":\"$FID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-02T12:00:00\",\"clockOut\":\"2026-11-02T13:00:00\"}" 400
[[ "$(field .error)" == "This overlaps Frank R's time on Mon, Nov 2"* ]] || { echo "FAIL: overlap message wrong: $BODY"; exit 1; }
call POST /api/time "$O" "{\"userId\":\"$FID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-09T08:00:00\"}" 400
call POST /api/time "$O" "{\"userId\":\"$FID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-09T08:00:00\",\"clockOut\":\"2026-11-09T07:00:00\"}" 400
call POST /api/time "$O" "{\"userId\":\"$FID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-09T08:00:00\",\"clockOut\":\"2026-11-10T09:00:00\"}" 400
call POST /api/time "$O" "{\"userId\":\"$FID\",\"projectId\":\"$JOB\",\"clockIn\":\"2099-01-01T08:00:00\",\"clockOut\":\"2099-01-01T09:00:00\"}" 400
call POST /api/time "$O" "{\"userId\":\"$FID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-09T08:00:00\",\"clockOut\":\"2026-11-09T09:00:00\",\"breakMinutes\":90}" 400
call POST /api/time "$O" "{\"userId\":\"$BID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-09T08:00:00\",\"clockOut\":\"2026-11-09T09:00:00\"}" 400
call POST /api/time "$O" "{\"userId\":\"$FID\",\"clockIn\":\"2026-11-09T08:00:00\",\"clockOut\":\"2026-11-09T09:00:00\"}" 400
call POST /api/time "$F" "{\"userId\":\"$FID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-09T08:00:00\",\"clockOut\":\"2026-11-09T09:00:00\"}" 403
call GET "/api/time?from=2026-11-02&to=2026-11-08&userId=$FID" "$O" "" 200
[[ "$(field '.entries | length')" == "6" && "$(field '.people | length')" == "1" ]] || { echo "FAIL: one person's week wrong: $BODY"; exit 1; }
[[ "$(field '.people[0] | [.minutes, .regular, .overtime, .doubleTime, .approved] == [3060, 2400, 600, 60, 0]')" == "true" ]] || { echo "FAIL: overtime math wrong: $(field .people)"; exit 1; }
call GET "/api/time?from=2026-11-02&to=2026-11-08" "$O" "" 200
[[ "$(field '.people | length')" == "2" && "$(field '.jobs | length')" == "1" && "$(field '.jobs[0].minutes')" == "3300" ]] || { echo "FAIL: the company's week wrong: $BODY"; exit 1; }
call GET "/api/time?from=2026-11-02&to=2026-11-08&userId=$AID" "$F" "" 200
[[ "$(echo "$BODY" | jq -r --arg f "$FID" '(.entries | length) == 6 and all(.entries[]; .userId == $f)')" == "true" ]] || { echo "FAIL: field crew should see only their own time: $BODY"; exit 1; }
call GET "/api/time?from=2026-11-08&to=2026-11-02" "$O" "" 400
call GET "/api/time?from=2026-11-01&to=2027-01-15" "$O" "" 400
call PUT "/api/time/$TSAT" "$O" "{\"userId\":\"$FID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-07T07:00:00\",\"clockOut\":\"2026-11-07T12:00:00\"}" 200
[[ "$(field .minutes)" == "300" ]] || { echo "FAIL: time edit not saved: $BODY"; exit 1; }
call PUT "/api/time/$TSAT" "$F" "{\"userId\":\"$FID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-07T07:00:00\",\"clockOut\":\"2026-11-07T13:00:00\"}" 403
call GET "/api/time?from=2026-11-02&to=2026-11-08&userId=$FID" "$O" "" 200
WEEK="$(field '[.entries[].id]')"
call POST /api/time/approve "$O" "{\"entryIds\":$WEEK}" 200
[[ "$(field 'all(.[]; .approved and .approvedBy == "Olivia")')" == "true" ]] || { echo "FAIL: approving didn't approve: $BODY"; exit 1; }
call PUT "/api/time/$TSAT" "$O" "{\"userId\":\"$FID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-07T07:00:00\",\"clockOut\":\"2026-11-07T13:00:00\"}" 409
call DELETE "/api/time/$TSAT" "$O" "" 409
call POST /api/time/approve "$F" "{\"entryIds\":$WEEK}" 403
call POST /api/time/approve "$O" '{"entryIds":[]}' 400
call POST /api/time/approve "$O" "{\"entryIds\":[\"$TSAT\"],\"approved\":false}" 200
call DELETE "/api/time/$TSAT" "$O" "" 204
call GET "/api/time?from=2026-11-02&to=2026-11-08&userId=$FID" "$O" "" 200
[[ "$(field '.entries | length')" == "5" && "$(field '.people[0].approved')" == "2820" ]] || { echo "FAIL: approved total wrong after unapproving and deleting: $BODY"; exit 1; }
getfile "/api/time/export?from=2026-11-02&to=2026-11-08" "$O" 200
[[ "$CTYPE" == text/csv* ]] && grep -q "Date,Person,Job number,Job,Clock in" /tmp/smoke_file && grep -q "Frank R" /tmp/smoke_file && grep -q "'=SUM(A1)" /tmp/smoke_file \
  && grep -q "^Frank R,47.00,40.00,6.00,1.00,47.00" /tmp/smoke_file || { echo "FAIL: timesheet download wrong:"; cat /tmp/smoke_file; exit 1; }
PASS=$((PASS + 1)); echo "ok   timesheet download"
getfile "/api/time/export" "$F" 403
call POST /api/time/clock-in "$F" "{\"projectId\":\"$JOB\"}" 200
FT3="$(field .id)"
call POST /api/time/approve "$O" "{\"entryIds\":[\"$FT3\"]}" 400
call GET /api/time/now "$B" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: company B sees company A's clock"; exit 1; }
call GET "/api/time?from=2026-11-02&to=2026-11-08&userId=$FID" "$B" "" 200
[[ "$(field '.entries | length')" == "0" ]] || { echo "FAIL: company B sees company A's time"; exit 1; }
call POST /api/time/clock-in "$B" "{\"projectId\":\"$JOB\"}" 400
call PUT "/api/time/$TMON" "$B" "{\"userId\":\"$BID\",\"projectId\":\"$JOB\",\"clockIn\":\"2026-11-02T07:00:00\",\"clockOut\":\"2026-11-02T08:00:00\"}" 404
call DELETE "/api/time/$TMON" "$B" "" 404
call POST /api/time/approve "$B" "{\"entryIds\":[\"$TMON\"],\"approved\":false}" 404
call DELETE "/api/projects/$JOB2" "$A" "" 204
call GET /api/time "$O" "" 200
[[ "$(echo "$BODY" | jq -r --arg t "$FT2" '.entries[] | select(.id == $t) | [.projectId, .jobName] == [null, "#5226 Garage ADU"]')" == "true" ]] || { echo "FAIL: time should outlive its deleted job: $BODY"; exit 1; }
call GET "/api/activity?limit=60" "$A" "" 200
for t in "clocked in" "switched to this job from #5225 Kitchen + pantry remodel" "clocked out (0 min)" "added 10 h for Frank R on Mon, Nov 2" "changed Frank R's time on Sat, Nov 7" "approved 52 h of Frank R's time" "deleted Frank R's time on Sat, Nov 7"; do
  [[ "$(echo "$BODY" | jq -r --arg t "$t" 'map(.text) | index($t) != null')" == "true" ]] || { echo "FAIL: activity is missing: $t"; exit 1; }
done
call GET "/api/activity?limit=100" "$F" "" 200
[[ "$(field 'map(select(.kind == "time")) | length')" == "0" ]] || { echo "FAIL: field crew shouldn't see time clock lines: $BODY"; exit 1; }
PASS=$((PASS + 1)); echo "ok   time clock changes show in the activity, for the office only"

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
call GET "/api/projects/$JOB" "$A" "" 200
[[ "$(echo "$BODY" | jq -r --arg a "$AID" '.managerIds == [$a]')" == "true" ]] || { echo "FAIL: a removed member should no longer manage jobs: $BODY"; exit 1; }
call GET "/api/projects/$JOB/schedule" "$A" "" 200
[[ "$(echo "$BODY" | jq -r --arg f "$FID" '[.[].assigneeIds[]] | index($f) == null')" == "true" ]] || { echo "FAIL: a removed member is still assigned on the schedule: $BODY"; exit 1; }
call GET /api/projects "$F" "" 401
call POST /api/auth/login "" "{\"email\":\"field$RUN@example.com\",\"password\":\"changeme123\"}" 401
call GET "/api/projects/$JOB/logs" "$A" "" 200
[[ "$(field 'length')" == "2" ]] || { echo "FAIL: removed member's log should stay"; exit 1; }
call GET /api/time/now "$A" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: a removed member should be clocked out: $BODY"; exit 1; }
call GET "/api/time?userId=$FID" "$A" "" 200
[[ "$(echo "$BODY" | jq -r --arg t "$FT3" '[.entries[] | select(.id == $t and .clockOut != null)] | length == 1')" == "true" ]] || { echo "FAIL: a removed member's time should stay, clocked out: $BODY"; exit 1; }

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
[[ "$(field .number)" == "1001" ]] || { echo "FAIL: each company's job numbers should start at 1001: $BODY"; exit 1; }
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
[[ "$(field .date)" == "$(TZ=America/Los_Angeles date +%F)" ]] || { echo "FAIL: a log without a date should get today's date in Pacific time: $BODY"; exit 1; }
upload "/api/logs/$LOG2/photos" "$A" 201 "$PNG"
PHOTO3="$(field '.[0].id')"
call POST "/api/projects/$JOB/todos" "$A" '{"title":"Second to-do"}' 201
call DELETE "/api/schedule/$SCH2" "$A" "" 204
call GET "/api/projects/$JOB/schedule" "$A" "" 200
[[ "$(field 'length')" == "1" ]] || { echo "FAIL: schedule item not deleted: $BODY"; exit 1; }
call POST /api/projects "$A" '{"name":"Next job"}' 201
[[ "$(field .number)" == "5226" ]] || { echo "FAIL: the next job number should follow the highest one: $BODY"; exit 1; }
call DELETE "/api/projects/$(field .id)" "$A" "" 204
call DELETE "/api/projects/$JOB" "$A" "" 409
call DELETE "/api/payments/$PAY2" "$A" "" 204
call PUT "/api/invoices/$INV1/status" "$A" '{"status":0}' 200
call PUT "/api/estimates/$ESTID/status" "$A" '{"status":0}' 200
call DELETE "/api/projects/$JOB" "$A" "" 409
call PUT "/api/estimates/$CO1/status" "$A" '{"status":0}' 200
call DELETE "/api/projects/$JOB" "$A" "" 204
call GET "/api/projects/$JOB/logs" "$A" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: logs left behind after job delete"; exit 1; }
call GET "/api/activity?projectId=$JOB&limit=1" "$A" "" 200
[[ "$(field '.[0].text')" == "deleted the job" && "$(field '.[0].jobName')" == "#5225 Kitchen + pantry remodel" ]] || { echo "FAIL: deleted job should stay in the activity with its name: $BODY"; exit 1; }
call GET /api/todos "$A" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: to-dos left behind after job delete"; exit 1; }
call GET "/api/schedule?from=2026-12-07&to=2026-12-20" "$A" "" 200
[[ "$(field '.items | length')" == "0" ]] || { echo "FAIL: schedule left behind after job delete: $BODY"; exit 1; }
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

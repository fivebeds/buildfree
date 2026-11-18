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
call GET /api/projects "" "" 401

echo "== Clients"
call POST /api/clients "$A" '{"name":"Maria Lopez","email":"maria@example.com","phone":"619-555-0100"}' 201
CLIENT="$(field .id)"
call POST /api/clients "$A" '{"name":"   "}' 400
call POST /api/clients "$A" '{"name":"Bad Email","email":"nope"}' 400
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
call DELETE "/api/projects/$JOB" "$A" "" 204
call GET "/api/projects/$JOB/logs" "$A" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: logs left behind after job delete"; exit 1; }
call GET /api/todos "$A" "" 200
[[ "$(field 'length')" == "0" ]] || { echo "FAIL: to-dos left behind after job delete"; exit 1; }
getfile "/api/photos/$PHOTO3" "$A" 404

echo
echo "All $PASS checks passed."

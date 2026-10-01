#!/usr/bin/env bash
# Creates a venue, a 10 x 20 hall, an event and a published show. Prints the showId.
set -euo pipefail
CATALOG=${CATALOG:-http://localhost:5101}
ADMIN=(-H "Content-Type: application/json" -H "X-User-Id: admin-1" -H "X-User-Role: admin")
json() { python3 -c "import sys, json; print(json.load(sys.stdin)['$1'])"; }

VENUE=$(curl -fsS "${ADMIN[@]}" -d '{"name":"Riverside Arena","city":"Pune","timezone":"Asia/Kolkata"}' "$CATALOG/admin/venues" | json venueId)
HALL=$(curl -fsS "${ADMIN[@]}" -d '{"name":"Hall 1","grid":{"rowCount":10,"seatsPerRow":20,"defaultCategory":"SILVER","categoryByRow":{"A":"RECLINER","B":"RECLINER","C":"GOLD","D":"GOLD","E":"GOLD"}}}' "$CATALOG/admin/venues/$VENUE/halls" | json hallId)
EVENT=$(curl -fsS "${ADMIN[@]}" -d '{"title":"Midnight Echoes Live","category":"CONCERT","language":"en","durationMin":150}' "$CATALOG/admin/events" | json eventId)
STARTS=$(date -u -d '+7 days' +%Y-%m-%dT19:30:00Z 2>/dev/null || date -u -v+7d +%Y-%m-%dT19:30:00Z)
OPENS=$(date -u +%Y-%m-%dT%H:%M:%SZ)
SHOW=$(curl -fsS "${ADMIN[@]}" -d "{\"eventId\":\"$EVENT\",\"hallId\":\"$HALL\",\"startsAt\":\"$STARTS\",\"saleOpensAt\":\"$OPENS\",\"currency\":\"INR\",\"prices\":{\"RECLINER\":90000,\"GOLD\":50000,\"SILVER\":30000}}" "$CATALOG/admin/shows" | json showId)
curl -fsS "${ADMIN[@]}" -X POST "$CATALOG/admin/shows/$SHOW/publish" > /dev/null

echo "Published show: $SHOW"
echo "Waiting for Seat Inventory to open it for sale..."
sleep 3
echo "Open http://localhost:5105 and paste the show id."

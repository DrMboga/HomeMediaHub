#!/bin/bash
# Phase 1 verification — run from your Windows machine (Git Bash or WSL).
# Connects to the server over SSH and runs each check remotely.
#
# Usage:
#   bash infra/setup/verify-phase1.sh          # checks stage (default)
#   bash infra/setup/verify-phase1.sh prod     # checks prod

SERVER=mike@192.168.0.192
APP_DIR=/opt/homemediahub/app
ENV=${1:-stage}
ENV_FILE=".env.$ENV"

PASS=0
FAIL=0

green='\033[0;32m'
red='\033[0;31m'
reset='\033[0m'

pass() { echo -e "  ${green}PASS${reset}  $1"; PASS=$((PASS+1)); }
fail() { echo -e "  ${red}FAIL${reset}  $1"; [ -n "$2" ] && echo "        $2"; FAIL=$((FAIL+1)); }

ssh_run() { ssh -o ConnectTimeout=5 -q "$SERVER" "$1" 2>&1; }
compose()  { ssh_run "cd $APP_DIR && docker compose --env-file $ENV_FILE $*"; }

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  HomeMediaHub — Phase 1 verification ($ENV)"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""

# ── 1. SSH reachable ───────────────────────────────────────────────────────
echo "[1] SSH connectivity"
if ssh -o ConnectTimeout=5 -q "$SERVER" "echo ok" 2>/dev/null | grep -q ok; then
    pass "Server reachable at $SERVER"
else
    fail "Cannot SSH to $SERVER — check IP and that sshd is running"
    echo ""
    echo "Aborting: cannot reach server."
    exit 1
fi

# ── 2. Docker installed ────────────────────────────────────────────────────
echo ""
echo "[2] Docker"
ver=$(ssh_run "docker --version 2>/dev/null")
if echo "$ver" | grep -q "Docker version"; then
    pass "Docker installed: $ver"
else
    fail "Docker not found — run server-setup.sh first"
fi

compose_ver=$(ssh_run "docker compose version 2>/dev/null")
if echo "$compose_ver" | grep -q "Docker Compose"; then
    pass "Docker Compose plugin: $compose_ver"
else
    fail "Docker Compose plugin missing"
fi

# ── 3. Env file exists ─────────────────────────────────────────────────────
echo ""
echo "[3] Environment file"
if ssh_run "test -f $APP_DIR/$ENV_FILE && echo ok" | grep -q ok; then
    pass "$ENV_FILE exists"
else
    fail "$ENV_FILE not found at $APP_DIR/$ENV_FILE" \
         "Run: cp .env.$ENV.example .env.$ENV && nano .env.$ENV"
fi

# ── 4. Containers running ──────────────────────────────────────────────────
echo ""
echo "[4] Container status"
expected_services="postgres immich-postgres redis immich-server n8n api ai-sidecar frontend nginx"
ps_output=$(compose ps --format "table {{.Service}}\t{{.Status}}" 2>/dev/null)

for svc in $expected_services; do
    line=$(echo "$ps_output" | grep "^$svc")
    if echo "$line" | grep -qiE "running|healthy|Up"; then
        pass "$svc is running"
    else
        status=$(echo "$line" | awk '{print $2}' | head -1)
        fail "$svc not running" "Status: ${status:-not found}"
    fi
done

# ── 5. Postgres healthy ────────────────────────────────────────────────────
echo ""
echo "[5] PostgreSQL health"
pg_result=$(compose exec -T postgres pg_isready 2>/dev/null)
if echo "$pg_result" | grep -q "accepting connections"; then
    pass "Postgres accepting connections"
else
    fail "Postgres not ready" "$pg_result"
fi

# ── 6. Database schema ─────────────────────────────────────────────────────
echo ""
echo "[6] Database schema (homemediahub_db)"
tables=$(compose exec -T postgres \
    psql -U postgres -d homemediahub_db -c "\dt" --tuples-only 2>/dev/null)

expected_tables="media_files media_metadata media_tags media_embeddings media_thumbnails albums album_media video_transcodes photo_series processing_jobs"
for tbl in $expected_tables; do
    if echo "$tables" | grep -q "$tbl"; then
        pass "Table: $tbl"
    else
        fail "Table missing: $tbl"
    fi
done

# ── 7. pgvector extension ──────────────────────────────────────────────────
echo ""
echo "[7] pgvector extension"
ext=$(compose exec -T postgres \
    psql -U postgres -d homemediahub_db -c "SELECT extname FROM pg_extension WHERE extname='vector';" --tuples-only 2>/dev/null)
if echo "$ext" | grep -q "vector"; then
    pass "pgvector extension installed"
else
    fail "pgvector extension missing"
fi

# ── 8. API health ──────────────────────────────────────────────────────────
echo ""
echo "[8] Service health endpoints (from inside server)"
api_resp=$(ssh_run "curl -sf http://localhost:8080/health")
if echo "$api_resp" | grep -q '"status"'; then
    pass ".NET API /health → $api_resp"
else
    fail ".NET API not responding on :8080" "$api_resp"
fi

ai_resp=$(ssh_run "curl -sf http://localhost:8000/health")
if echo "$ai_resp" | grep -q '"status"'; then
    pass "AI sidecar /health → $ai_resp"
else
    fail "AI sidecar not responding on :8000" "$ai_resp"
fi

nginx_resp=$(ssh_run "curl -sf -o /dev/null -w '%{http_code}' http://localhost:80/")
if [ "$nginx_resp" = "200" ]; then
    pass "NGINX responding on :80 (HTTP $nginx_resp)"
else
    fail "NGINX not responding on :80" "HTTP $nginx_resp"
fi

# ── Summary ────────────────────────────────────────────────────────────────
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
total=$((PASS+FAIL))
echo "  Results: $PASS/$total passed"
if [ "$FAIL" -eq 0 ]; then
    echo -e "  ${green}Phase 1 COMPLETE ✓${reset}"
else
    echo -e "  ${red}$FAIL check(s) failed — see details above${reset}"
fi
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""

[ "$FAIL" -eq 0 ]

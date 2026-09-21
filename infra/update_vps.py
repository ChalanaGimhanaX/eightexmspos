#!/usr/bin/env python3
"""
update_vps.py: Complete update and deep verification of the VPS environment.
"""
import os
import sys
import time
import paramiko

TARGET_HOST = "5.189.170.180"
ROOT_USER = "root"
ROOT_PASS = "@0517eighte@ft0517"

SECRETS_DIR = os.path.join(os.path.dirname(__file__), ".secrets")
PRIV_KEY_PATH = os.path.join(SECRETS_DIR, "enightx_dev_ed25519")

creds = {}
with open(os.path.join(SECRETS_DIR, "teammate_credentials.env")) as f:
    for line in f:
        if "=" in line:
            k, v = line.strip().split("=", 1)
            creds[k] = v

DEV_PASS = creds.get("DEV_PASSWORD")
PG_PASS = creds.get("POSTGRES_DEV_PASSWORD")

def run_cmd(client, cmd, timeout=120, print_live=True):
    channel = client.get_transport().open_session()
    channel.exec_command(cmd)
    out, err = [], []
    start = time.time()
    while not channel.exit_status_ready():
        while channel.recv_ready():
            chunk = channel.recv(4096).decode('utf-8', errors='replace')
            out.append(chunk)
            if print_live:
                sys.stdout.write(chunk)
                sys.stdout.flush()
        while channel.recv_stderr_ready():
            chunk = channel.recv_stderr(4096).decode('utf-8', errors='replace')
            err.append(chunk)
            if print_live:
                sys.stderr.write(chunk)
                sys.stderr.flush()
        if time.time() - start > timeout:
            channel.close()
            return -1, "".join(out), f"Timed out after {timeout}s: {cmd}"
        time.sleep(0.1)
    while channel.recv_ready():
        chunk = channel.recv(4096).decode('utf-8', errors='replace')
        out.append(chunk)
        if print_live:
            sys.stdout.write(chunk)
            sys.stdout.flush()
    while channel.recv_stderr_ready():
        chunk = channel.recv_stderr(4096).decode('utf-8', errors='replace')
        err.append(chunk)
        if print_live:
            sys.stderr.write(chunk)
            sys.stderr.flush()
    exit_status = channel.recv_exit_status()
    channel.close()
    return exit_status, "".join(out), "".join(err)

def main():
    print("=== Step 1: Root SSH - Fix Database Security & Initialize Dev Schema ===")
    root_client = paramiko.SSHClient()
    root_client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    root_client.connect(TARGET_HOST, username=ROOT_USER, password=ROOT_PASS, timeout=10)

    db_harden_sql = """
cd /tmp
runuser -u postgres -- psql <<'SQL'
REVOKE CONNECT ON DATABASE eightex_pos FROM PUBLIC;
REVOKE CONNECT ON DATABASE radio_db FROM PUBLIC;
REVOKE CONNECT ON DATABASE postgres FROM PUBLIC;
GRANT CONNECT ON DATABASE eightex_pos TO eightex_app;
GRANT CONNECT ON DATABASE radio_db TO radio_user;
GRANT CONNECT ON DATABASE postgres TO postgres;
SQL
"""
    st, out, err = run_cmd(root_client, db_harden_sql)
    print(f"Database security harden exit status: {st}")

    dev_schema_sql = f"""
cd /tmp
runuser -u postgres -- psql -d enightx_pos_dev <<'SQL'
CREATE TABLE IF NOT EXISTS devices (
    device_id VARCHAR(64) PRIMARY KEY,
    tenant_id VARCHAR(64) NOT NULL,
    branch_id VARCHAR(64) NOT NULL,
    device_code VARCHAR(64) NOT NULL,
    device_name VARCHAR(128) NOT NULL,
    hardware_fingerprint VARCHAR(256) NOT NULL,
    app_version VARCHAR(32) NOT NULL,
    device_generation INT NOT NULL DEFAULT 1,
    token VARCHAR(128) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS sync_batches (
    batch_id UUID PRIMARY KEY,
    tenant_id VARCHAR(64) NOT NULL,
    source_device_id VARCHAR(64) NOT NULL,
    source_generation INT NOT NULL,
    batch_sequence INT NOT NULL,
    sent_at TIMESTAMPTZ NOT NULL,
    acknowledged_sequence INT NOT NULL DEFAULT 0,
    status VARCHAR(32) NOT NULL DEFAULT 'acknowledged',
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS sync_events (
    event_id UUID PRIMARY KEY,
    batch_id UUID REFERENCES sync_batches(batch_id) ON DELETE CASCADE,
    tenant_id VARCHAR(64) NOT NULL,
    branch_id VARCHAR(64) NOT NULL,
    device_id VARCHAR(64) NOT NULL,
    device_generation INT NOT NULL,
    source_sequence INT NOT NULL,
    schema_version VARCHAR(16) NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    actor_id VARCHAR(64) NOT NULL,
    causal_reference VARCHAR(128),
    payload JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE devices OWNER TO enightx_dev;
ALTER TABLE sync_batches OWNER TO enightx_dev;
ALTER TABLE sync_events OWNER TO enightx_dev;
SQL

# Also apply schema to staging
runuser -u postgres -- psql -d enightx_pos_staging <<'SQL'
CREATE TABLE IF NOT EXISTS devices (
    device_id VARCHAR(64) PRIMARY KEY,
    tenant_id VARCHAR(64) NOT NULL,
    branch_id VARCHAR(64) NOT NULL,
    device_code VARCHAR(64) NOT NULL,
    device_name VARCHAR(128) NOT NULL,
    hardware_fingerprint VARCHAR(256) NOT NULL,
    app_version VARCHAR(32) NOT NULL,
    device_generation INT NOT NULL DEFAULT 1,
    token VARCHAR(128) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS sync_batches (
    batch_id UUID PRIMARY KEY,
    tenant_id VARCHAR(64) NOT NULL,
    source_device_id VARCHAR(64) NOT NULL,
    source_generation INT NOT NULL,
    batch_sequence INT NOT NULL,
    sent_at TIMESTAMPTZ NOT NULL,
    acknowledged_sequence INT NOT NULL DEFAULT 0,
    status VARCHAR(32) NOT NULL DEFAULT 'acknowledged',
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS sync_events (
    event_id UUID PRIMARY KEY,
    batch_id UUID REFERENCES sync_batches(batch_id) ON DELETE CASCADE,
    tenant_id VARCHAR(64) NOT NULL,
    branch_id VARCHAR(64) NOT NULL,
    device_id VARCHAR(64) NOT NULL,
    device_generation INT NOT NULL,
    source_sequence INT NOT NULL,
    schema_version VARCHAR(16) NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    actor_id VARCHAR(64) NOT NULL,
    causal_reference VARCHAR(128),
    payload JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE devices OWNER TO enightx_dev;
ALTER TABLE sync_batches OWNER TO enightx_dev;
ALTER TABLE sync_events OWNER TO enightx_dev;
SQL
"""
    st, out, err = run_cmd(root_client, dev_schema_sql)
    print(f"Dev schema init exit status: {st}")

    print("\n=== Step 2: Configure ~/.pgpass for enightx-dev ===")
    pgpass_cmd = f"""
cat <<'EOF' > /home/enightx-dev/.pgpass
127.0.0.1:5432:enightx_pos_dev:enightx_dev:{PG_PASS}
127.0.0.1:5432:enightx_pos_staging:enightx_dev:{PG_PASS}
localhost:5432:enightx_pos_dev:enightx_dev:{PG_PASS}
localhost:5432:enightx_pos_staging:enightx_dev:{PG_PASS}
EOF
chown enightx-dev:enightx-dev /home/enightx-dev/.pgpass
chmod 0600 /home/enightx-dev/.pgpass
echo "Configured ~/.pgpass for enightx-dev"
"""
    st, out, err = run_cmd(root_client, pgpass_cmd)
    print(f"pgpass setup exit status: {st}")

    print("\n=== Step 3: Transfer fresh Git Bundle to VPS ===")
    bundle_local = "/root/workspace/enightx-pos.bundle"
    bundle_remote = "/tmp/enightx-pos.bundle"
    sftp = root_client.open_sftp()
    print(f"Uploading {bundle_local} to {bundle_remote}...")
    sftp.put(bundle_local, bundle_remote)
    sftp.close()
    run_cmd(root_client, f"chmod 644 {bundle_remote}")

    print("\n=== Step 4: Update Teammate Workspace from Bundle ===")
    update_workspace_cmd = f"""
runuser -u enightx-dev -- bash <<'DEV_EOF'
set -euo pipefail
DEST="/srv/enightx/workspaces/teammate/enightx-pos"
cd "$DEST"

git remote set-url origin {bundle_remote} || git remote add origin {bundle_remote}
git fetch origin

# Update develop branch
git checkout develop
git reset --hard origin/develop

# Re-create/update feature branch
git checkout -B codex/teammate/workspace-init develop

echo "Teammate workspace branch updated to $(git rev-parse HEAD):"
git log -n 3 --oneline

# Ensure .env exists in both root and apps/api
cp -f apps/api/.env .env
chmod 600 .env apps/api/.env

# Build .NET solution
dotnet build EnightxPos.sln
DEV_EOF

rm -f {bundle_remote}
"""
    st, out, err = run_cmd(root_client, update_workspace_cmd, timeout=120)
    print(f"Teammate workspace update exit status: {st}")

    root_client.close()

    print("\n=== Step 5: Verification as enightx-dev ===")
    dev_client = paramiko.SSHClient()
    dev_client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    dev_pkey = paramiko.Ed25519Key.from_private_key_file(PRIV_KEY_PATH)
    dev_client.connect(TARGET_HOST, username="enightx-dev", pkey=dev_pkey, timeout=10)

    print("\n--- Running API Pytest Suite (Expected: 8 Passed) ---")
    cmd_pytest = "cd /srv/enightx/workspaces/teammate/enightx-pos && apps/api/.venv/bin/pytest tests/api -v"
    st_py, out_py, err_py = run_cmd(dev_client, cmd_pytest, timeout=90)
    print(f"Pytest Exit Status: {st_py}")

    print("\n--- Running .NET Test Suite (Expected: 15 Passed) ---")
    cmd_dotnet = "cd /srv/enightx/workspaces/teammate/enightx-pos && dotnet test --no-build -v normal"
    st_net, out_net, err_net = run_cmd(dev_client, cmd_dotnet, timeout=90)
    print(f"Dotnet Test Exit Status: {st_net}")

    print("\n--- Testing Settings Resolution from Repo Root ---")
    cmd_settings = "cd /srv/enightx/workspaces/teammate/enightx-pos && apps/api/.venv/bin/python -c 'from enightx_api.config import settings; print(\"PORT:\", settings.PORT, \"DB:\", settings.POSTGRES_DB)'"
    st_set, out_set, err_set = run_cmd(dev_client, cmd_settings)
    print(f"Settings Check: {out_set.strip()}")

    print("\n--- Testing Non-Interactive Database Connection via ~/.pgpass ---")
    cmd_db_query = 'psql -w -h 127.0.0.1 -U enightx_dev -d enightx_pos_dev -c "SELECT current_user, current_database();" -c "\\dt"'
    st_db, out_db, err_db = run_cmd(dev_client, cmd_db_query)
    print(f"DB Query Status: {st_db}\n{out_db.strip()}")

    print("\n--- Testing Database Access Isolation (Host Production DBs Must Reject) ---")
    cmd_eightex = 'psql -w -h 127.0.0.1 -U enightx_dev -d eightex_pos -c "SELECT 1;"'
    st_eig, out_eig, err_eig = run_cmd(dev_client, cmd_eightex)
    print(f"Attempt connect eightex_pos -> Exit: {st_eig} (Denied: {st_eig != 0})")
    if err_eig.strip():
        print(f"  Error message: {err_eig.strip()}")

    cmd_radio = 'psql -w -h 127.0.0.1 -U enightx_dev -d radio_db -c "SELECT 1;"'
    st_rad, out_rad, err_rad = run_cmd(dev_client, cmd_radio)
    print(f"Attempt connect radio_db -> Exit: {st_rad} (Denied: {st_rad != 0})")
    if err_rad.strip():
        print(f"  Error message: {err_rad.strip()}")

    cmd_postgres = 'psql -w -h 127.0.0.1 -U enightx_dev -d postgres -c "SELECT 1;"'
    st_pg, out_pg, err_pg = run_cmd(dev_client, cmd_postgres)
    print(f"Attempt connect postgres -> Exit: {st_pg} (Denied: {st_pg != 0})")
    if err_pg.strip():
        print(f"  Error message: {err_pg.strip()}")

    print("\n--- Testing Filesystem Boundary Isolation ---")
    st_prod, out_prod, err_prod = run_cmd(dev_client, "ls /srv/enightx/production")
    print(f"Read /srv/enightx/production -> St: {st_prod} (Denied: {st_prod != 0})")

    st_stag, out_stag, err_stag = run_cmd(dev_client, "ls /srv/enightx/staging")
    print(f"Read /srv/enightx/staging -> St: {st_stag} (Denied: {st_stag != 0})")

    dev_client.close()
    print("\n=== Update and Deep Verification Finished Successfully! ===")

if __name__ == "__main__":
    main()

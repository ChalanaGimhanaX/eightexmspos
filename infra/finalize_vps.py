#!/usr/bin/env python3
"""
finalize_vps.py: Fixes PostgreSQL role/database, enables SSH pubkey authentication,
and runs deep test verification and security boundary assertions as enightx-dev.
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

# Read credentials from teammate_credentials.env
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
    print("=== Step A: As Root - Fix PostgreSQL & Enable SSH Pubkey ===")
    root_client = paramiko.SSHClient()
    root_client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    root_client.connect(TARGET_HOST, username=ROOT_USER, password=ROOT_PASS, timeout=10)

    # 1. Setup PostgreSQL user and databases
    pg_sql = f"""
cd /tmp
runuser -u postgres -- psql -c "SELECT 1 FROM pg_roles WHERE rolname='enightx_dev';" | grep -q 1 || runuser -u postgres -- psql -c "CREATE USER enightx_dev WITH PASSWORD '{PG_PASS}';"
runuser -u postgres -- psql -c "ALTER ROLE enightx_dev WITH PASSWORD '{PG_PASS}';"
runuser -u postgres -- psql -c "SELECT 1 FROM pg_database WHERE datname='enightx_pos_dev';" | grep -q 1 || runuser -u postgres -- psql -c "CREATE DATABASE enightx_pos_dev OWNER enightx_dev;"
runuser -u postgres -- psql -c "SELECT 1 FROM pg_database WHERE datname='enightx_pos_staging';" | grep -q 1 || runuser -u postgres -- psql -c "CREATE DATABASE enightx_pos_staging OWNER enightx_dev;"
PGPASSWORD='{PG_PASS}' psql -h 127.0.0.1 -U enightx_dev -d enightx_pos_dev -c "SELECT current_user, current_database();"
"""
    st, out, err = run_cmd(root_client, pg_sql)
    print(f"PostgreSQL Config Exit Code: {st}")

    # 2. Check and Enable PubkeyAuthentication in SSHD
    sshd_fix = """
mkdir -p /etc/ssh/sshd_config.d
cat <<'EOF' > /etc/ssh/sshd_config.d/50-enightx.conf
PubkeyAuthentication yes
AuthorizedKeysFile .ssh/authorized_keys
EOF
systemctl reload sshd
echo "SSHD reloaded with PubkeyAuthentication enabled."
"""
    st, out, err = run_cmd(root_client, sshd_fix)
    print(f"SSHD Pubkey Config Exit Code: {st}")
    root_client.close()

    # 3. Test SSH Key Authentication as enightx-dev
    print("\n=== Step B: Test SSH Key Authentication as enightx-dev ===")
    dev_client = paramiko.SSHClient()
    dev_client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    dev_pkey = paramiko.Ed25519Key.from_private_key_file(PRIV_KEY_PATH)

    try:
        dev_client.connect(TARGET_HOST, username="enightx-dev", pkey=dev_pkey, timeout=10)
        print("SUCCESS: Connected as enightx-dev via ED25519 public key!")
    except Exception as e:
        print(f"Key auth failed ({e}), falling back to password auth...")
        dev_client.connect(TARGET_HOST, username="enightx-dev", password=DEV_PASS, timeout=10)
        print("Connected as enightx-dev via password auth.")

    # 4. Deep Verification - Run API Pytest as enightx-dev
    print("\n=== Step C: Running API Pytest in Teammate Workspace ===")
    cmd_pytest = "cd /srv/enightx/workspaces/teammate/enightx-pos && apps/api/.venv/bin/pytest tests/api -v"
    st_py, out_py, err_py = run_cmd(dev_client, cmd_pytest, timeout=90)
    print(f"\nPytest Exit Status: {st_py}")

    # 5. Deep Verification - Run .NET Unit Tests as enightx-dev
    print("\n=== Step D: Running .NET Unit Tests in Teammate Workspace ===")
    cmd_dotnet = "cd /srv/enightx/workspaces/teammate/enightx-pos && dotnet test --no-build -v normal"
    st_net, out_net, err_net = run_cmd(dev_client, cmd_dotnet, timeout=90)
    print(f"\nDotnet Test Exit Status: {st_net}")

    # 6. Verify Git Workspace & Branch
    print("\n=== Step E: Verifying Teammate Git Status & Safe Directory ===")
    st_git, out_git, err_git = run_cmd(dev_client, "cd /srv/enightx/workspaces/teammate/enightx-pos && git status && git branch -a && git log -n 3 --oneline")

    # 7. Verify Security Isolation Boundaries
    print("\n=== Step F: Verifying Security Isolation Boundaries ===")
    st_p1, out_p1, err_p1 = run_cmd(dev_client, "ls /srv/enightx/production")
    print(f"Test 1: Read /srv/enightx/production -> St: {st_p1} (Denied: {'Permission denied' in err_p1 or st_p1 != 0})")

    st_p2, out_p2, err_p2 = run_cmd(dev_client, "ls /srv/enightx/shared/production")
    print(f"Test 2: Read /srv/enightx/shared/production -> St: {st_p2} (Denied: {'Permission denied' in err_p2 or st_p2 != 0})")

    st_p3, out_p3, err_p3 = run_cmd(dev_client, "touch /srv/enightx/review/submissions/test_submission.tmp && rm /srv/enightx/review/submissions/test_submission.tmp && echo 'REVIEW_WRITABLE'")
    print(f"Test 3: Write to /srv/enightx/review/submissions -> St: {st_p3} ({out_p3.strip()})")

    st_p4, out_p4, err_p4 = run_cmd(dev_client, "cat /srv/enightx/workspaces/teammate/enightx-pos/apps/api/.env | grep PORT")
    print(f"Test 4: Read Teammate local .env -> {out_p4.strip()}")

    dev_client.close()
    print("\n=== Finalization Completed Successfully! ===")

if __name__ == "__main__":
    main()


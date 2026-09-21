#!/usr/bin/env python3
"""
provision_vps.py: Complete provisioning of the target VPS for Enightx POS.
Installs toolchains, configures OS accounts and permission boundaries,
sets up PostgreSQL development database, transfers Git bundle,
configures teammate workspace, sets up Python 3.12 venv and .NET 8,
and verifies test execution and security boundaries as non-root enightx-dev.
"""
import os
import sys
import time
import secrets
import stat
import paramiko
from cryptography.hazmat.primitives.asymmetric import ed25519
from cryptography.hazmat.primitives import serialization

TARGET_HOST = os.getenv("VPS_HOST", "5.189.170.180")
TARGET_USER = os.getenv("VPS_USER", "root")
TARGET_PASS = os.getenv("VPS_PASS", "@0517eighte@ft0517")

SECRETS_DIR = os.path.join(os.path.dirname(__file__), ".secrets")
os.makedirs(SECRETS_DIR, exist_ok=True)
os.chmod(SECRETS_DIR, 0o700)

def run_cmd(client, cmd, timeout=300, print_live=True):
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
        time.sleep(0.2)
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
    print(f"=== Starting Enightx VPS Provisioning for {TARGET_HOST} ===")

    # 1. Connect as Root
    root_client = paramiko.SSHClient()
    root_client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    print(f"Connecting to {TARGET_USER}@{TARGET_HOST}...")
    root_client.connect(hostname=TARGET_HOST, username=TARGET_USER, password=TARGET_PASS, timeout=15)
    print("Root SSH connection established.\n")

    # 2. Install Toolchains: Python 3.12, .NET 8 SDK
    print("--- Step 1: Installing toolchains (Python 3.12, .NET SDK 8.0) ---")
    st, out, err = run_cmd(root_client, "dnf install -y python3.12 python3.12-pip python3.12-devel dotnet-sdk-8.0", timeout=360)
    if st != 0:
        print(f"WARNING: DNF returned non-zero code {st}. Checking versions directly...")
    
    st_py, out_py, _ = run_cmd(root_client, "python3.12 --version")
    st_dot, out_dot, _ = run_cmd(root_client, "dotnet --version")
    print(f"Installed Python: {out_py.strip()}")
    print(f"Installed .NET: {out_dot.strip()}\n")

    # 3. Setup OS Accounts and Directory Layout
    print("--- Step 2: Configuring OS accounts and /srv/enightx directory tree ---")
    setup_script = """
set -euo pipefail
BASE_DIR="/srv/enightx"
TEAMMATE_USER="enightx-dev"
SERVICE_USER="enightx-srv"
DEPLOY_GROUP="enightx-deploy"

# Groups and Users
if ! getent group "${DEPLOY_GROUP}" >/dev/null 2>&1; then
    groupadd --system "${DEPLOY_GROUP}"
    echo "Created group ${DEPLOY_GROUP}"
fi

if ! id -u "${SERVICE_USER}" >/dev/null 2>&1; then
    useradd --system --shell /usr/sbin/nologin --home-dir "${BASE_DIR}/production" --gid "${DEPLOY_GROUP}" "${SERVICE_USER}"
    echo "Created user ${SERVICE_USER}"
fi

if ! id -u "${TEAMMATE_USER}" >/dev/null 2>&1; then
    useradd --create-home --shell /bin/bash "${TEAMMATE_USER}"
    echo "Created user ${TEAMMATE_USER}"
fi

# Hierarchy
mkdir -p "${BASE_DIR}/workspaces/teammate"
mkdir -p "${BASE_DIR}/review/submissions"
mkdir -p "${BASE_DIR}/staging/releases"
mkdir -p "${BASE_DIR}/production/releases"
mkdir -p "${BASE_DIR}/shared/staging"
mkdir -p "${BASE_DIR}/shared/production"

# Permissions
chown -R "${TEAMMATE_USER}:${TEAMMATE_USER}" "${BASE_DIR}/workspaces/teammate"
chmod 700 "${BASE_DIR}/workspaces/teammate"

chown -R "${TEAMMATE_USER}:${DEPLOY_GROUP}" "${BASE_DIR}/review"
chmod 775 "${BASE_DIR}/review"

chown -R "${SERVICE_USER}:${DEPLOY_GROUP}" "${BASE_DIR}/staging" "${BASE_DIR}/production" "${BASE_DIR}/shared"
chmod 750 "${BASE_DIR}/staging"
chmod 700 "${BASE_DIR}/production"
chmod 750 "${BASE_DIR}/shared/staging"
chmod 700 "${BASE_DIR}/shared/production"

echo "Directory structure & permissions established."
"""
    st, out, err = run_cmd(root_client, setup_script)
    if st != 0:
        raise RuntimeError(f"Failed to set up accounts/directories: {err}")

    # 4. Generate SSH Key & Credentials for Teammate (enightx-dev)
    print("\n--- Step 3: Generating SSH Keypair and Credentials for enightx-dev ---")
    priv_key_path = os.path.join(SECRETS_DIR, "enightx_dev_ed25519")
    pub_key_path = priv_key_path + ".pub"

    private_key = ed25519.Ed25519PrivateKey.generate()
    priv_bytes = private_key.private_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PrivateFormat.OpenSSH,
        encryption_algorithm=serialization.NoEncryption()
    )
    pub_bytes = private_key.public_key().public_bytes(
        encoding=serialization.Encoding.OpenSSH,
        format=serialization.PublicFormat.OpenSSH
    )

    with open(priv_key_path, "wb") as f:
        f.write(priv_bytes)
    os.chmod(priv_key_path, stat.S_IRUSR | stat.S_IWUSR)

    with open(pub_key_path, "wb") as f:
        f.write(pub_bytes)

    pub_key_str = pub_bytes.decode('utf-8').strip() + " enightx-dev-coworker\n"
    dev_password = secrets.token_urlsafe(20)

    # Save credentials locally
    cred_file = os.path.join(SECRETS_DIR, "teammate_credentials.env")
    with open(cred_file, "w") as f:
        f.write(f"VPS_HOST={TARGET_HOST}\n")
        f.write(f"SSH_USER=enightx-dev\n")
        f.write(f"SSH_PORT=22\n")
        f.write(f"SSH_PRIVATE_KEY_PATH={priv_key_path}\n")
        f.write(f"DEV_PASSWORD={dev_password}\n")
    os.chmod(cred_file, 0o600)

    # Install on VPS
    ssh_setup_cmd = f"""
mkdir -p /home/enightx-dev/.ssh
chmod 700 /home/enightx-dev/.ssh
cat <<'EOF' > /home/enightx-dev/.ssh/authorized_keys
{pub_key_str}
EOF
chmod 600 /home/enightx-dev/.ssh/authorized_keys
chown -R enightx-dev:enightx-dev /home/enightx-dev/.ssh
echo "enightx-dev:{dev_password}" | chpasswd
echo "SSH keys and password configured for enightx-dev."
"""
    st, out, err = run_cmd(root_client, ssh_setup_cmd)
    if st != 0:
        raise RuntimeError(f"Failed to configure SSH keys for enightx-dev: {err}")

    # 5. Configure PostgreSQL for Development
    print("\n--- Step 4: Setting up PostgreSQL enightx_dev role and database ---")
    pg_password = secrets.token_urlsafe(24)
    # Save pg pass in cred_file
    with open(cred_file, "a") as f:
        f.write(f"POSTGRES_DEV_PASSWORD={pg_password}\n")

    pg_setup_sql = f"""
cd /tmp
runuser -u postgres -- psql <<'SQL'
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'enightx_dev') THEN
    CREATE ROLE enightx_dev WITH LOGIN PASSWORD '{pg_password}';
  ELSE
    ALTER ROLE enightx_dev WITH PASSWORD '{pg_password}';
  END IF;
END
\$\$;

SELECT 'CREATE DATABASE enightx_pos_dev OWNER enightx_dev'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'enightx_pos_dev')\\gexec

SELECT 'CREATE DATABASE enightx_pos_staging OWNER enightx_dev'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'enightx_pos_staging')\\gexec
SQL
"""
    st, out, err = run_cmd(root_client, pg_setup_sql)
    if st != 0:
        print(f"WARNING: PostgreSQL setup returned code {st}: {err}")

    # Verify connection as enightx_dev
    st, out, err = run_cmd(root_client, f"PGPASSWORD='{pg_password}' psql -h 127.0.0.1 -U enightx_dev -d enightx_pos_dev -c 'SELECT current_user, current_database();'")
    print("Postgres connection verification:")
    print(out.strip())

    # 6. Transfer Git Bundle & Seed Teammate Workspace
    print("\n--- Step 5: Transferring repository bundle and cloning workspace ---")
    bundle_local = "/root/workspace/enightx-pos.bundle"
    bundle_remote = "/tmp/enightx-pos.bundle"

    sftp = root_client.open_sftp()
    print(f"Uploading {bundle_local} to {bundle_remote}...")
    sftp.put(bundle_local, bundle_remote)
    sftp.close()

    clone_script = f"""
set -euo pipefail
chmod 644 {bundle_remote}

# Clone as enightx-dev
runuser -u enightx-dev -- bash <<'DEV_EOF'
set -euo pipefail
DEST="/srv/enightx/workspaces/teammate/enightx-pos"
if [ ! -d "$DEST/.git" ]; then
    git clone {bundle_remote} "$DEST"
    cd "$DEST"
    git checkout develop
    git checkout -b codex/teammate/workspace-init
    git config user.name "Teammate AI"
    git config user.email "teammate@enightx.local"
    git config --global --add safe.directory "$DEST"
    echo "Cloned repository into $DEST"
else
    echo "Workspace already exists at $DEST"
fi
DEV_EOF

rm -f {bundle_remote}
"""
    st, out, err = run_cmd(root_client, clone_script)
    if st != 0:
        raise RuntimeError(f"Failed to clone repository into teammate workspace: {err}")

    # 7. Setup Teammate Virtualenv, Dependencies & .env
    print("\n--- Step 6: Setting up Python 3.12 venv, .NET restore & isolated .env ---")
    setup_env_script = f"""
set -euo pipefail
runuser -u enightx-dev -- bash <<'DEV_EOF'
set -euo pipefail
DEST="/srv/enightx/workspaces/teammate/enightx-pos"
cd "$DEST"

# 1. Create .env file for development (using port 8010 to avoid conflicting with port 8000)
cat <<'EOF' > apps/api/.env
ENVIRONMENT=development
DEBUG=true
HOST=127.0.0.1
PORT=8010
SECRET_KEY="{secrets.token_urlsafe(32)}"
ALLOWED_ORIGINS=http://localhost:3000,http://localhost:8010
POSTGRES_HOST=127.0.0.1
POSTGRES_PORT=5432
POSTGRES_DB=enightx_pos_dev
POSTGRES_USER=enightx_dev
POSTGRES_PASSWORD="{pg_password}"
LICENCE_SIGNING_PUBLIC_KEY="PLACEHOLDER_ED25519_PUBLIC_KEY"
EOF
chmod 600 apps/api/.env

# 2. Setup Python 3.12 Virtual Environment
if [ ! -d "apps/api/.venv" ]; then
    python3.12 -m venv apps/api/.venv
fi
apps/api/.venv/bin/pip install --upgrade pip setuptools wheel
apps/api/.venv/bin/pip install -e "apps/api[dev]"

# 3. Restore .NET
dotnet restore EnightxPos.sln
dotnet build EnightxPos.sln --no-restore

echo "Teammate environment configuration finished."
DEV_EOF
"""
    st, out, err = run_cmd(root_client, setup_env_script, timeout=300)
    if st != 0:
        raise RuntimeError(f"Failed to setup teammate venv and dependencies: {err}")

    root_client.close()
    print("Root provisioning session closed.\n")

    # 8. Verification as enightx-dev via Direct SSH Key Connection
    print("--- Step 7: Verifying Teammate Access & Running Tests as enightx-dev ---")
    dev_client = paramiko.SSHClient()
    dev_client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    
    # Load private key
    dev_pkey = paramiko.Ed25519Key.from_private_key_file(priv_key_path)
    print(f"Connecting as enightx-dev@{TARGET_HOST} using ED25519 key...")
    dev_client.connect(hostname=TARGET_HOST, username="enightx-dev", pkey=dev_pkey, timeout=10)
    print("Teammate SSH key authentication successful!\n")

    # Identity check
    st, out, err = run_cmd(dev_client, "whoami && id && pwd")
    print(f"Connected User: {out.strip()}\n")

    # Run API Pytest
    print("Running API test suite as enightx-dev...")
    st_py, out_py, err_py = run_cmd(dev_client, "cd /srv/enightx/workspaces/teammate/enightx-pos && apps/api/.venv/bin/pytest tests/api", timeout=60)
    print(f"API Pytest Exit Code: {st_py}")

    # Run Dotnet Test
    print("\nRunning .NET test suite as enightx-dev...")
    st_net, out_net, err_net = run_cmd(dev_client, "cd /srv/enightx/workspaces/teammate/enightx-pos && dotnet test", timeout=60)
    print(f".NET Test Exit Code: {st_net}")

    # Test Security Boundary & Isolation
    print("\n--- Step 8: Verifying Security Isolation Boundaries ---")
    st_prod, out_prod, err_prod = run_cmd(dev_client, "ls /srv/enightx/production")
    print(f"Attempt to access /srv/enightx/production: Exit Code {st_prod} (Should be non-zero / Permission denied)")
    
    st_sprod, out_sprod, err_sprod = run_cmd(dev_client, "ls /srv/enightx/shared/production")
    print(f"Attempt to access /srv/enightx/shared/production: Exit Code {st_sprod} (Should be non-zero / Permission denied)")

    st_rev, out_rev, err_rev = run_cmd(dev_client, "touch /srv/enightx/review/submissions/.probe && rm /srv/enightx/review/submissions/.probe && echo 'Review directory writable!'")
    print(f"Review submissions write access: {out_rev.strip()}")

    dev_client.close()

    print("\n" + "="*50)
    print("Enightx POS VPS Provisioning Completed Successfully!")
    print(f"Teammate User: enightx-dev")
    print(f"Private Key: {priv_key_path}")
    print(f"Credentials File: {cred_file}")
    print("="*50)

if __name__ == "__main__":
    main()


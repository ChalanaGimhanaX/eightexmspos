#!/usr/bin/env python3
"""
setup_posapi_service.py:
1. Updates teammate workspace git origin to https://github.com/ChalanaGimhanaX/eightexmspos.git
   and ensures branches main, develop, codex/dev/phase0-phase1-sale are checked out.
2. Checks git remote connectivity and credential requirements on VPS.
3. Deploys production service under /srv/enightx/production with isolated service user enightx-srv.
4. Creates systemd service enightx-pos-api and enables/starts it on port 8010.
5. Obtains Let's Encrypt SSL certificate for posapi.eightexms.site via certbot.
6. Deploys Nginx reverse proxy configuration for posapi.eightexms.site.
7. Performs end-to-end verification of https://posapi.eightexms.site/health.
"""
import os
import sys
import time
import paramiko

TARGET_HOST = "5.189.170.180"
ROOT_USER = "root"
ROOT_PASS = "@0517eighte@ft0517"

SECRETS_DIR = os.path.join(os.path.dirname(__file__), ".secrets")
creds = {}
cred_path = os.path.join(SECRETS_DIR, "teammate_credentials.env")
if os.path.exists(cred_path):
    with open(cred_path) as f:
        for line in f:
            if "=" in line:
                k, v = line.strip().split("=", 1)
                creds[k] = v

DEV_PASS = creds.get("DEV_PASSWORD", "")
PG_PASS = creds.get("POSTGRES_DEV_PASSWORD", "")

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
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    print(f"Connecting to root@{TARGET_HOST}...")
    client.connect(TARGET_HOST, username=ROOT_USER, password=ROOT_PASS, timeout=10)
    print("Connected successfully.\n")

    # Step 1: Update teammate workspace git remote and branches
    print("=== Step 1: Update teammate workspace git remote and branches ===")
    step1_script = """
set -euo pipefail
WORKSPACE="/srv/enightx/workspaces/teammate/enightx-pos"

# Update origin URL
runuser -u enightx-dev -- git -C "$WORKSPACE" remote set-url origin https://github.com/ChalanaGimhanaX/eightexmspos.git
runuser -u enightx-dev -- git -C "$WORKSPACE" remote -v

# Ensure branches exist locally
for br in develop main codex/dev/phase0-phase1-sale; do
    if ! runuser -u enightx-dev -- git -C "$WORKSPACE" show-ref --verify --quiet "refs/heads/$br"; then
        runuser -u enightx-dev -- git -C "$WORKSPACE" branch "$br" "origin/$br" || true
    fi
done

runuser -u enightx-dev -- git -C "$WORKSPACE" branch -a
"""
    st, out, err = run_cmd(client, step1_script)
    if st != 0:
        print(f"Step 1 failed with exit code {st}")
        return st

    # Step 2: Test remote connectivity from teammate workspace
    print("\n=== Step 2: Test remote connectivity from teammate workspace ===")
    step2_script = """
WORKSPACE="/srv/enightx/workspaces/teammate/enightx-pos"
echo "Testing GIT_TERMINAL_PROMPT=0 git ls-remote origin as enightx-dev..."
if runuser -u enightx-dev -- env GIT_TERMINAL_PROMPT=0 git -C "$WORKSPACE" ls-remote origin; then
    echo "SUCCESS: Remote is publicly accessible without credentials."
else
    echo "REQUIREMENT CONFIRMED: Credentials (GitHub PAT / SSH Key) are required to access or push to origin."
fi
"""
    st, out, err = run_cmd(client, step2_script)

    # Step 3: Deploy production runtime under /srv/enightx/production
    print("\n=== Step 3: Deploy production runtime under /srv/enightx/production ===")
    step3_script = f"""
set -euo pipefail
PROD_DIR="/srv/enightx/production"
RELEASE_DIR="/srv/enightx/production/releases/v1.0.0"
SRC_WORKSPACE="/srv/enightx/workspaces/teammate/enightx-pos"

mkdir -p "$RELEASE_DIR"
# Copy application code into immutable release directory
cp -a "$SRC_WORKSPACE/apps" "$RELEASE_DIR/"
cp -a "$SRC_WORKSPACE/contracts" "$RELEASE_DIR/"
cp -a "$SRC_WORKSPACE/README.md" "$RELEASE_DIR/"

# Setup symlink to current
ln -sfn "$RELEASE_DIR" "$PROD_DIR/current"

# Create Python 3.12 virtual environment for production service
if [ ! -d "$PROD_DIR/venv" ]; then
    /usr/bin/python3.12 -m venv "$PROD_DIR/venv"
fi

# Install dependencies into production venv
"$PROD_DIR/venv/bin/pip" install --upgrade pip
"$PROD_DIR/venv/bin/pip" install -e "$RELEASE_DIR/apps/api"

# Configure production .env
cat << 'EOF' > "$RELEASE_DIR/apps/api/.env"
ENVIRONMENT=production
DEBUG=false
HOST=127.0.0.1
PORT=8010
SECRET_KEY="production_enightx_pos_api_key_secure_v1"
ALLOWED_ORIGINS=https://posapi.eightexms.site,https://pos.eightexms.site,http://localhost:3000
POSTGRES_HOST=127.0.0.1
POSTGRES_PORT=5432
POSTGRES_DB=enightx_pos_staging
POSTGRES_USER=enightx_dev
POSTGRES_PASSWORD="{PG_PASS}"
LICENCE_SIGNING_PUBLIC_KEY="PLACEHOLDER_ED25519_PUBLIC_KEY"
EOF

cp "$RELEASE_DIR/apps/api/.env" "$RELEASE_DIR/.env"

# Set strict permissions: owned by enightx-srv:enightx-deploy
chown -R enightx-srv:enightx-deploy "$PROD_DIR"
chmod 700 "$PROD_DIR"
chmod 750 "$RELEASE_DIR"
chmod 600 "$RELEASE_DIR/apps/api/.env" "$RELEASE_DIR/.env"
"""
    st, out, err = run_cmd(client, step3_script)
    if st != 0:
        print(f"Step 3 failed with exit code {st}")
        return st

    # Step 4: Create and Start systemd service enightx-pos-api
    print("\n=== Step 4: Configure & Start systemd service enightx-pos-api ===")
    step4_script = """
set -euo pipefail

cat << 'EOF' > /etc/systemd/system/enightx-pos-api.service
[Unit]
Description=Enightx POS Cloud Synchronization & Management API
After=network.target postgresql.service
Wants=postgresql.service

[Service]
Type=simple
User=enightx-srv
Group=enightx-deploy
WorkingDirectory=/srv/enightx/production/current
Environment=PYTHONUNBUFFERED=1
ExecStart=/srv/enightx/production/venv/bin/uvicorn src.enightx_api.main:app --app-dir apps/api --host 127.0.0.1 --port 8010
Restart=always
RestartSec=5
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable enightx-pos-api
systemctl restart enightx-pos-api

# Wait for service to come up
sleep 2
systemctl status enightx-pos-api --no-pager
curl -s http://127.0.0.1:8010/health
"""
    st, out, err = run_cmd(client, step4_script)
    if st != 0:
        print(f"Step 4 failed with exit code {st}")
        return st

    # Step 5: Obtain Let's Encrypt SSL certificate for posapi.eightexms.site
    print("\n=== Step 5: Obtain Let's Encrypt SSL certificate ===")
    step5_script = """
set -euo pipefail

# Obtain certificate via certbot nginx plugin
certbot certonly --nginx -d posapi.eightexms.site --non-interactive --agree-tos --email chalana@nimalmotors.lk || \
certbot certonly --nginx -d posapi.eightexms.site --non-interactive --agree-tos --register-unsafely-without-email
"""
    st, out, err = run_cmd(client, step5_script)
    if st != 0:
        print(f"Step 5 failed with exit code {st}")
        return st

    # Step 6: Configure Nginx reverse proxy for posapi.eightexms.site
    print("\n=== Step 6: Configure Nginx reverse proxy for posapi.eightexms.site ===")
    step6_script = """
set -euo pipefail

cat << 'EOF' > /etc/nginx/conf.d/posapi.eightexms.site.conf
server {
    server_name posapi.eightexms.site;

    client_max_body_size 50M;

    location / {
        proxy_pass http://127.0.0.1:8010;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 600s;
        proxy_connect_timeout 60s;
        proxy_send_timeout 600s;
    }

    listen 443 ssl;
    ssl_certificate /etc/letsencrypt/live/posapi.eightexms.site/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/posapi.eightexms.site/privkey.pem;
    include /etc/letsencrypt/options-ssl-nginx.conf;
    ssl_dhparam /etc/letsencrypt/ssl-dhparams.pem;
}

server {
    listen 80;
    server_name posapi.eightexms.site;
    return 301 https://$host$request_uri;
}
EOF

nginx -t
systemctl reload nginx
"""
    st, out, err = run_cmd(client, step6_script)
    if st != 0:
        print(f"Step 6 failed with exit code {st}")
        return st

    # Step 7: Verify public endpoint
    print("\n=== Step 7: Verify public endpoint https://posapi.eightexms.site/health ===")
    step7_script = """
curl -i https://posapi.eightexms.site/health
"""
    st, out, err = run_cmd(client, step7_script)
    print(f"\nVerification exit code: {st}")

    client.close()
    return st

if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""
deploy_production_api.py:
Deploys updated API code to /srv/enightx/production/releases/v1.0.2 via pure Paramiko SFTP,
updates current symlink, and restarts enightx-pos-api.service.
"""
import os
import time
import paramiko

TARGET_HOST = "5.189.170.180"
ROOT_USER = "root"
ROOT_PASS = "@0517eighte@ft0517"
WORKSPACE = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))

def sftp_mkdir_p(sftp, remote_dir):
    dirs = []
    d = remote_dir
    while d and d != "/":
        dirs.append(d)
        d = os.path.dirname(d)
    for d in reversed(dirs):
        try:
            sftp.mkdir(d)
        except IOError:
            pass

def sftp_upload_dir(sftp, local_dir, remote_dir):
    sftp_mkdir_p(sftp, remote_dir)
    for root, dirs, files in os.walk(local_dir):
        if "__pycache__" in root or ".venv" in root or ".git" in root or ".pytest_cache" in root:
            continue
        rel_root = os.path.relpath(root, local_dir)
        target_root = remote_dir if rel_root == "." else os.path.join(remote_dir, rel_root)
        sftp_mkdir_p(sftp, target_root)
        for f in files:
            if f.endswith(".pyc") or f == ".DS_Store":
                continue
            local_path = os.path.join(root, f)
            remote_path = os.path.join(target_root, f)
            sftp.put(local_path, remote_path)
            print(f"  Uploaded: {os.path.relpath(local_path, WORKSPACE)} -> {remote_path}")

def run_remote(client, cmd):
    chan = client.get_transport().open_session()
    chan.exec_command(cmd)
    out = []
    while not chan.exit_status_ready():
        if chan.recv_ready():
            out.append(chan.recv(4096).decode("utf-8", errors="replace"))
        time.sleep(0.1)
    while chan.recv_ready():
        out.append(chan.recv(4096).decode("utf-8", errors="replace"))
    return chan.recv_exit_status(), "".join(out)

def main():
    print(f"--> Connecting to root@{TARGET_HOST}...")
    c = paramiko.SSHClient()
    c.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    c.connect(TARGET_HOST, username=ROOT_USER, password=ROOT_PASS, timeout=15)
    print("    Connected successfully.")

    sftp = c.open_sftp()
    target_rel = "/srv/enightx/production/releases/v1.0.7"
    print(f"--> Uploading source files to {target_rel} via SFTP...")

    # Upload apps/api/src
    sftp_upload_dir(sftp, os.path.join(WORKSPACE, "apps/api/src"), os.path.join(target_rel, "apps/api/src"))

    # Upload pyproject.toml
    sftp_mkdir_p(sftp, os.path.join(target_rel, "apps/api"))
    sftp.put(os.path.join(WORKSPACE, "apps/api/pyproject.toml"), os.path.join(target_rel, "apps/api/pyproject.toml"))

    # Upload contracts
    sftp_upload_dir(sftp, os.path.join(WORKSPACE, "contracts"), os.path.join(target_rel, "contracts"))

    sftp.close()
    print("--> SFTP upload complete.")

    # Finalize permissions, write .env, and restart systemd service
    setup_cmd = f"""
cat <<'EOF' > {target_rel}/apps/api/.env
ENVIRONMENT=production
DEBUG=false
HOST=127.0.0.1
PORT=8010
SECRET_KEY="production_enightx_pos_api_key_secure_v1"
ALLOWED_ORIGINS=https://posapi.eightexms.site,https://pos.eightexms.site,http://localhost:3000
POSTGRES_HOST=127.0.0.1
POSTGRES_PORT=5432
POSTGRES_DB=enightx_pos
POSTGRES_USER=enightx_user
POSTGRES_PASSWORD=dev_password
LICENCE_SIGNING_PUBLIC_KEY="PLACEHOLDER_ED25519_PUBLIC_KEY"
ENIGHTX_RELEASE_TOKEN="secure_release_token_enightx_2026"
ENIGHTX_RELEASE_MANIFEST="/srv/enightx/downloads/version-v2.json"
EOF
cp -f {target_rel}/apps/api/.env {target_rel}/.env
chown -R enightx-srv:enightx-deploy {target_rel}
chmod 600 {target_rel}/apps/api/.env {target_rel}/.env
ln -sfn {target_rel} /srv/enightx/production/current
systemctl restart enightx-pos-api
systemctl status enightx-pos-api --no-pager
"""
    st, out = run_remote(c, setup_cmd)
    print(out)
    if st != 0:
        print(f"ERROR: Service restart failed with exit code {st}")
        return

    time.sleep(2)
    st_health, out_health = run_remote(c, "curl -s http://127.0.0.1:8010/health")
    print(f"--> Production Health Check: {out_health}")
    st_cat, out_cat = run_remote(c, "curl -s http://127.0.0.1:8010/api/v1/sync/catalog")
    print(f"--> Production Catalog Sync Check: {out_cat}")
    c.close()
    print("\n✅ Production API deployed successfully!")

if __name__ == "__main__":
    main()

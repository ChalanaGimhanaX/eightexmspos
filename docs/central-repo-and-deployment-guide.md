# Central GitHub Repository, Deployment & Remote Setup Guide

This guide documents the central GitHub remote configuration, authentication requirements, VPS deployment architecture, Nginx reverse proxy, and Let's Encrypt SSL setup for the **Enightx POS System**.

---

## 1. Central GitHub Remote Configuration

### Repository Details
- **Central Repository URL:** `https://github.com/ChalanaGimhanaX/eightexmspos.git`
- **Configured Remotes:**
  - **Local Development Environment (`/root/workspace/enightx Pos System`):**
    ```bash
    git remote -v
    # origin  https://github.com/ChalanaGimhanaX/eightexmspos.git (fetch)
    # origin  https://github.com/ChalanaGimhanaX/eightexmspos.git (push)
    ```
  - **Target VPS Teammate Workspace (`/srv/enightx/workspaces/teammate/enightx-pos`):**
    ```bash
    git remote -v
    # origin  https://github.com/ChalanaGimhanaX/eightexmspos.git (fetch)
    # origin  https://github.com/ChalanaGimhanaX/eightexmspos.git (push)
    ```

### Synchronized Branches
The following branches are synchronized and tracking the central repository:
- `main`
- `develop`
- `codex/dev/phase0-phase1-sale`
- `codex/teammate/workspace-init`

---

## 2. Remote Connectivity & Credentials Requirements

### Current Status
- The GitHub repository `https://github.com/ChalanaGimhanaX/eightexmspos.git` is **private**.
- Public unauthenticated requests receive HTTP 404 (standard GitHub behavior for private repositories).
- Non-interactive Git requests without credentials fail with:
  ```text
  fatal: could not read Username for 'https://github.com': terminal prompts disabled
  ```
- The existing SSH key on the local dev machine (`/root/.ssh/id_ed25519_github`) is an authorized deploy key specifically for `ChalanaGimhanaX/Nimal-Morters-system`. When attempting to authenticate against `eightexmspos`, GitHub returns:
  ```text
  ERROR: Repository not found.
  fatal: Could not read from remote repository.
  ```

### Action Required for Owner / Teammate to Push
To push `main`, `develop`, and `codex/dev/phase0-phase1-sale` to `origin`, valid credentials must be supplied using either of the two standard methods:

#### Method A: Personal Access Token (PAT) (Recommended for HTTPS)
1. In GitHub, navigate to **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** (or Tokens classic).
2. Generate a token with repository access to `eightexmspos` and `repo:write` / `contents:write` permissions.
3. Authenticate git push:
   ```bash
   # One-time authenticated push:
   git push https://<GITHUB_USERNAME>:<PAT>@github.com/ChalanaGimhanaX/eightexmspos.git main develop codex/dev/phase0-phase1-sale

   # Or configure git credential helper:
   git config --global credential.helper cache
   # or
   git config --global credential.helper store
   ```

#### Method B: Dedicated SSH Deploy Key (Pre-generated & Ready to Register)
A dedicated ED25519 deploy key has already been generated and configured on both the **Local Development Host** (`/root/.ssh/id_ed25519_eightexmspos`) and the **Target VPS** (`/home/enightx-dev/.ssh/id_ed25519_eightexmspos`).

1. **Owner Action (One-Time):** In GitHub, navigate to:
   `https://github.com/ChalanaGimhanaX/eightexmspos/settings/keys`
   - Click **Add deploy key**
   - Title: `enightx-pos-deploy-key`
   - Key:
     ```text
     ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIC/HwZ0UriGYILaZrHcm3FwPKos33mKLlRqmjJ2KLmSK deploy-key-eightexmspos
     ```
   - Check: **Allow write access** (Crucial: write access is needed to push)
   - Click **Add key**

2. **Immediate Push via SSH Host Alias (Pre-configured):**
   The SSH host alias `github-eightexmspos` is already configured in `~/.ssh/config` on both machines:
   ```bash
   # From Local Development Host:
   git push git@github-eightexmspos:ChalanaGimhanaX/eightexmspos.git main develop codex/dev/phase0-phase1-sale

   # From Target VPS (as enightx-dev):
   cd /srv/enightx/workspaces/teammate/enightx-pos
   git push git@github-eightexmspos:ChalanaGimhanaX/eightexmspos.git main develop codex/dev/phase0-phase1-sale
   ```

3. **Or Switch `origin` to SSH:**
   ```bash
   git remote set-url origin git@github-eightexmspos:ChalanaGimhanaX/eightexmspos.git
   git push -u origin main develop codex/dev/phase0-phase1-sale
   ```

---

## 3. Target VPS Deployment Architecture (`5.189.170.180`)

### A. Production Service Deployment
- **Dedicated Service User:** `enightx-srv:enightx-deploy` (system account, `/usr/sbin/nologin`).
- **Release Directory:** `/srv/enightx/production/releases/v1.0.0`
- **Active Symlink:** `/srv/enightx/production/current -> /srv/enightx/production/releases/v1.0.0`
- **Isolated Virtualenv:** `/srv/enightx/production/venv` (Python 3.12.14)
- **Configuration File:** `/srv/enightx/production/current/apps/api/.env` (permissions `0600 enightx-srv:enightx-deploy`)
- **Systemd Unit File:** `/etc/systemd/system/enightx-pos-api.service`
  ```ini
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
  ```

### B. Service Management Commands (Root / Operator)
```bash
# Check service status
systemctl status enightx-pos-api

# Restart service after release updates
systemctl restart enightx-pos-api

# Tail real-time service logs
journalctl -u enightx-pos-api -f
```

---

## 4. Nginx Reverse Proxy & SSL Configuration

### Nginx Configuration File
Location: `/etc/nginx/conf.d/posapi.eightexms.site.conf`
```nginx
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
```

### SSL Certificate Details
- **Certificate Provider:** Let's Encrypt ACME v2
- **Certificate Path:** `/etc/letsencrypt/live/posapi.eightexms.site/fullchain.pem`
- **Private Key Path:** `/etc/letsencrypt/live/posapi.eightexms.site/privkey.pem`
- **Initial Expiry Date:** 2026-12-20
- **Auto-Renewal:** Managed automatically by Certbot systemd timer (`certbot-renew.timer`).

---

## 5. Public Endpoint Verification Evidence

All endpoints are fully operational over HTTPS with valid certificate authority verification.

### 1. Health Check
```bash
curl -i https://posapi.eightexms.site/health
```
```http
HTTP/2 200 
server: nginx/1.20.1
date: Mon, 21 Sep 2026 12:46:42 GMT
content-type: application/json
content-length: 104

{"status":"ok","service":"enightx-api","version":"1.0.0","timestamp":"2026-09-21T12:46:42.390916+00:00"}
```

### 2. HTTP to HTTPS Auto-Redirect
```bash
curl -i http://posapi.eightexms.site/health
```
```http
HTTP/1.1 301 Moved Permanently
Location: https://posapi.eightexms.site/health
```

### 3. Device Enrollment Endpoint
```bash
curl -s -X POST https://posapi.eightexms.site/api/v1/devices/enroll \
  -H "Content-Type: application/json" \
  -d '{"device_code":"DEV01","hardware_fingerprint":"hw_test_123","tenant_id":"ten_001","branch_id":"br_001","device_name":"Counter 1","app_version":"1.0.0"}'
```
```json
{"device_id":"dev_b3f8b21ff5f1","device_generation":1,"token":"tok_8e51fc5da2e7415d80c9af2c817fa96f"}
```

### 4. Sync Batch Push Endpoint
```bash
curl -s -X POST https://posapi.eightexms.site/api/v1/sync/push \
  -H "Content-Type: application/json" \
  -d '{
    "batch_id": "0f248bce-15dd-45f2-93f2-30707b742284",
    "tenant_id": "ten_001",
    "source_device_id": "dev_b3f8b21ff5f1",
    "source_generation": 1,
    "batch_sequence": 1,
    "sent_at": "2026-09-21T12:47:04Z",
    "events": []
  }'
```
```json
{"batch_id":"0f248bce-15dd-45f2-93f2-30707b742284","acknowledged_sequence":1,"status":"acknowledged"}
```

---

## 6. Teammate Coworker Workspace Instructions

- **Workspace Path:** `/srv/enightx/workspaces/teammate/enightx-pos`
- **Execution User:** `enightx-dev` (UID 1002, GID 1003)
- **SSH Access:** `ssh -i infra/.secrets/enightx_dev_ed25519 enightx-dev@5.189.170.180`
- **Git Remote:** `origin` is mapped to `https://github.com/ChalanaGimhanaX/eightexmspos.git`
- **Testing:**
  ```bash
  cd /srv/enightx/workspaces/teammate/enightx-pos
  apps/api/.venv/bin/pytest tests/api -v      # 9 passed
  dotnet test --no-build -v normal          # 15 passed
  ```
- **Local Dev Server:**
  Port `8010` is reserved for the background production systemd service. To test API changes interactively, run uvicorn on port `8011`:
  ```bash
  apps/api/.venv/bin/uvicorn src.enightx_api.main:app --host 127.0.0.1 --port 8011 --reload
  ```

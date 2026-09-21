# Host Environment & Target VPS Inventory

**Date of Inspection & Provisioning:** 21 September 2026  
**Status:** Inspection completed. VPS provisioned with isolated teammate workspace and verified security boundaries.

---

## 1. Local Development Host

- **Host IP:** `107.175.156.78`
- **Operating System:** Ubuntu 24.04.3 LTS (Noble Numbat), Linux kernel 6.8.0-139-generic x86_64
- **Current Execution User:** `root` (uid=0, gid=0)
- **Memory & Storage:** ~31.3 GiB RAM, 1.1 TiB root partition (980 GiB free)
- **Toolchains:** Python 3.12.3, .NET SDK 8.0.131

---

## 2. Target VPS (`5.189.170.180`) Inventory

- **Hostname:** `vmi3191484.contaboserver.net`
- **Provider:** Contabo VPS
- **Operating System:** AlmaLinux 9.7 (Moss Jungle Cat), Linux kernel `5.14.0-611.49.2.el9_7.x86_64`
- **Architecture & Cores:** x86_64, 6 virtual cores (AMD EPYC Processor with IBPB)
- **Memory & Swap:**
  - Total RAM: 11,956 MiB (~12 GiB)
  - Swap: 4,095 MiB
- **Disk Storage:**
  - Root filesystem (`/dev/sda4`): 199 GB total, ~94 GB used, ~105 GB available (48% utilized).
- **Firewall & Security Modules:**
  - `firewalld`: Active (public zone allows 22/tcp, 80/tcp, 443/tcp, 3389/tcp, 5432/tcp, 7890/tcp, 2053/tcp).
  - SELinux: `disabled`.
- **Existing Pre-installed Services (Preserved without Interference):**
  - Web Server: Nginx (ports 80, 443)
  - SSH Server: OpenSSH 8.7 (port 22) - Pubkey and Password authentication active
  - Database: PostgreSQL 13.23 (port 5432) hosting existing `eightex_pos` and `radio_db`
  - MySQL / MariaDB: Port 3306 & 33060
  - Proxy & VPN Services: xray-linux-amd6 (ports 10001, 10002, 10003, 11111, 62789), x-ui (ports 2096, 41938)
  - Remote Desktop: xrdp (port 3389), Xvnc (port 5910)
  - Python Services: `eightexms` running Uvicorn on `127.0.0.1:8000` under PM2; llama-server on `8080`
  - Node Services: Next.js/PM2 applications on ports 3000, 3001, 3002, 3005, 3020, 3030, 3050, 3085
  - Containers: Podman 5.6.0 with Docker CLI emulation

---

## 3. Provisioned Enightx POS Architecture on VPS

### Directory Layout & Permissions
```text
/srv/enightx/
├── workspaces/
│   └── teammate/enightx-pos/   # 0700 enightx-dev:enightx-dev (private git clone & dev venv)
├── review/
│   └── submissions/            # 0775 enightx-dev:enightx-deploy (dev write, reviewers read)
├── staging/
│   └── releases/               # 0750 enightx-srv:enightx-deploy (immutable staging releases)
├── production/
│   └── releases/               # 0700 enightx-srv:enightx-deploy (immutable production releases)
└── shared/
    ├── staging/                # 0750 enightx-srv:enightx-deploy (staging runtime data)
    └── production/             # 0700 enightx-srv:enightx-deploy (production runtime data)
```

### OS Accounts
- **Teammate Developer:** `enightx-dev` (UID 1002, GID 1003), shell `/bin/bash`, home `/home/enightx-dev`.
- **Production Service User:** `enightx-srv` (system user), shell `/usr/sbin/nologin`, home `/srv/enightx/production`.
- **Deployment Group:** `enightx-deploy` (system group, GID 1001).

### Isolated Toolchains & Runtimes
1. **Python:** Python 3.12.14 installed (`/usr/bin/python3.12`).
   - Virtualenv created at `/srv/enightx/workspaces/teammate/enightx-pos/apps/api/.venv`.
   - Dependencies installed (`fastapi==0.115.0`, `pydantic==2.9.2`, `sqlalchemy==2.0.35`, `pytest==8.3.3`, etc.).
2. **.NET:** .NET SDK 8.0.131 installed (`/usr/bin/dotnet`).
   - Restored and built `EnightxPos.sln`.
3. **Database:** Dedicated PostgreSQL user `enightx_dev` and databases `enightx_pos_dev` and `enightx_pos_staging`.
4. **Port Allocation:** Enightx API configured to port `8010` (preserving port `8000` for existing `eightexms` service).

---

## 4. Verification & Security Boundary Evidence

- **Authentication:** `enightx-dev` connects directly via SSH ED25519 keypair and secure password without root privileges.
- **Deep Test Suite (Executed as non-root `enightx-dev` on VPS):**
  - API Pytest (`apps/api/.venv/bin/pytest tests/api -v`): **8 PASSED**, exit code 0 (100% pass rate).
  - .NET Tests (`dotnet test --no-build -v normal`): **15 PASSED**, 0 failed, exit code 0 (100% pass rate).
- **PostgreSQL Database Isolation & Hardening:**
  - `enightx_dev` database ownership: `enightx_pos_dev` and `enightx_pos_staging`.
  - Non-interactive auth: Preconfigured `~/.pgpass` (mode `0600`) in `/home/enightx-dev`.
  - Dev schema initialized: `devices`, `sync_batches`, `sync_events` tables created and owned by `enightx_dev`.
  - Public database connect revoked: Attempts by `enightx_dev` to access host databases (`eightex_pos`, `radio_db`, `postgres`) are strictly rejected by PostgreSQL authentication.
- **Filesystem Boundary Isolation Enforcement:**
  - `enightx-dev` reading `/srv/enightx/production`: **Permission denied** (exit code 255).
  - `enightx-dev` reading `/srv/enightx/staging`: **Permission denied** (exit code 255).
  - `enightx-dev` reading `/srv/enightx/shared/production`: **Permission denied** (exit code 255).
  - `enightx-dev` writing to `/srv/enightx/review/submissions`: **Permitted** (exit code 0).

---

## 5. Central GitHub Remote, Public Cloud API & SSL Deployment

### A. Central GitHub Remote Repository
- **Central Remote URL:** `https://github.com/ChalanaGimhanaX/eightexmspos.git`
- **Configured Locations:**
  1. Local Dev Host (`/root/workspace/enightx Pos System`): `origin` set to `https://github.com/ChalanaGimhanaX/eightexmspos.git`
  2. VPS Teammate Workspace (`/srv/enightx/workspaces/teammate/enightx-pos`): `origin` set to `https://github.com/ChalanaGimhanaX/eightexmspos.git`
- **Branches Tracked & Checked Out:**
  - `main`
  - `develop`
  - `codex/dev/phase0-phase1-sale`
  - `codex/teammate/workspace-init`
- **Remote Access & Credential Requirements:**
  - `eightexmspos` is a private GitHub repository.
  - Public unauthenticated requests receive HTTP 404 / terminal prompt disabled (`fatal: could not read Username for 'https://github.com'`).
  - Deploy keys for other repositories (e.g. `ChalanaGimhanaX/Nimal-Morters-system`) are strictly rejected by GitHub with `ERROR: Repository not found`.
  - **Required for Push:** The repository owner must provide a GitHub Personal Access Token (PAT) with `repo` scope OR register the pre-generated dedicated SSH Deploy Key (`ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIC/HwZ0UriGYILaZrHcm3FwPKos33mKLlRqmjJ2KLmSK deploy-key-eightexmspos`) with write permissions under repository settings (see `docs/central-repo-and-deployment-guide.md`).

### B. Production Service Deployment (`enightx-pos-api.service`)
- **Release Directory:** `/srv/enightx/production/releases/v1.0.0`
- **Current Symlink:** `/srv/enightx/production/current -> releases/v1.0.0`
- **Dedicated Python Virtualenv:** `/srv/enightx/production/venv` (Python 3.12.14)
- **Service User:** `enightx-srv:enightx-deploy` (system user with `/usr/sbin/nologin`)
- **Systemd Unit File:** `/etc/systemd/system/enightx-pos-api.service`
  - Active and enabled (`systemctl is-active enightx-pos-api` -> `active`)
  - Running Uvicorn on `127.0.0.1:8010`
  - Automatic restart on failure (`RestartSec=5`)

### C. Nginx Reverse Proxy & SSL Configuration
- **Public FQDN:** `posapi.eightexms.site`
- **DNS Resolution:** `5.189.170.180` (Contabo VPS)
- **Nginx Configuration:** `/etc/nginx/conf.d/posapi.eightexms.site.conf`
  - Port 80: HTTP 301 permanent redirect to `https://posapi.eightexms.site$request_uri`
  - Port 443: SSL reverse proxy to upstream `http://127.0.0.1:8010`
  - SSL Certificates: Let's Encrypt automated certificate (`/etc/letsencrypt/live/posapi.eightexms.site/fullchain.pem`, expires 2026-12-20)
  - Security Headers & Protocols: Modern TLS ciphers, WebSocket upgrade support, client max body size 50M

### D. Public Endpoint Verification
- `GET https://posapi.eightexms.site/health`
  - Response: `HTTP/2 200 OK`
  - Body: `{"status":"ok","service":"enightx-api","version":"1.0.0","timestamp":"..."}`
- `POST https://posapi.eightexms.site/api/v1/devices/enroll`
  - Response: `HTTP/2 200 OK`, issues persistent `device_id`, `device_generation: 1`, and security token.
- `POST https://posapi.eightexms.site/api/v1/sync/push`
  - Response: `HTTP/2 200 OK`, validates Pydantic UUID / batch schema and acknowledges sequence.


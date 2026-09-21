# Host Environment & Target VPS Inventory

**Date of Inspection:** 21 September 2026  
**Status:** Read-only inspection completed. No remote changes made.

---

## 1. Connected Development Host

- **Host IP:** `107.175.156.78`
- **Operating System:** Ubuntu 24.04.3 LTS (Noble Numbat), Linux kernel 6.8.0-139-generic x86_64
- **Current Execution User:** `root` (uid=0, gid=0)
- **Memory & Swap:**
  - Total RAM: 32,053 MiB (~31.3 GiB)
  - Available RAM: ~22,725 MiB
  - Swap: 8,191 MiB (0 used)
- **Disk Storage:**
  - Root partition (`/dev/mapper/ubuntu--vg-ubuntu--lv`): 1.1 TiB total, 72 GiB used (7%), 980 GiB free.
- **Firewall (UFW):** Active.
  - Allowed ports: 22, 80, 443, 3100, 5010, 5900, 6080, 8080, 15433, 18000, 20128.
- **Existing Host Applications & Containers:**
  - Docker containers:
    - Supabase self-hosted stack (Postgres 17.6 on port 15433, Kong on 18000/18443, PostgREST, Gotrue Auth, Studio, Storage, Realtime)
    - Open-WebUI (port 8080)
    - 9router proxy (port 20128)
  - System daemons:
    - Nginx reverse proxy (port 80)
    - OpenSSH (ports 22, 2222)
    - Tailscale VPN & Wireproxy
    - Remote display tools: x11vnc (5900), websockify (6080)
- **Installed Toolchains:**
  - Python: 3.12.3 with built-in SQLite 3.45.1 and pip 24.0
  - .NET: .NET SDK 8.0.131 (targeting `net8.0`, C# 12)

---

## 2. Target VPS (`5.189.170.180`) Status

- **IP Reachability:** Ping successful (ICMP echo round-trip ~115–170 ms).
- **SSH Service:** Port 22/tcp is reachable and open.
- **Connection Credentials:** **Not configured.** No SSH username, SSH private key, or password exists in local configuration for `5.189.170.180`.
- **VPS Provisioning State:** **Unverified / Unmodified.** In compliance with prompt safety instructions, no remote VPS modifications were attempted without verified credentials and senior review.

---

## 3. Resource & Security Isolation Rules

1. **No Shared Root/Mutated Workspace:** Development and testing are strictly scoped to isolated workspace trees. Teammate development will use dedicated non-root accounts.
2. **Ports & Services:** The Enightx POS FastAPI service will be mapped to dedicated non-conflicting internal ports (e.g. port 8000) and reverse-proxied with SSL; it will not collide with existing Supabase (15433/18000) or Open-WebUI (8080).
3. **No Secret Leakage:** No environment credentials, private keys, or tokens are committed or echoed in inspection logs.

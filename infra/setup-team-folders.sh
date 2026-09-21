#!/usr/bin/env bash
set -euo pipefail

# setup-team-folders.sh: Establishes isolated directory layout and permissions for Enightx POS on Linux VPS.
# Designed for execution by administrator during VPS onboarding.

BASE_DIR="/srv/enightx"
TEAMMATE_USER="enightx-dev"
SERVICE_USER="enightx-srv"
DEPLOY_GROUP="enightx-deploy"

echo "=== Setting up Enightx directory structure at ${BASE_DIR} ==="

# 1. Create service groups and users if they do not already exist
if ! getent group "${DEPLOY_GROUP}" >/dev/null 2>&1; then
    groupadd --system "${DEPLOY_GROUP}"
    echo "Created system group: ${DEPLOY_GROUP}"
fi

if ! id -u "${SERVICE_USER}" >/dev/null 2>&1; then
    useradd --system --shell /usr/sbin/nologin --home-dir "${BASE_DIR}/production" --gid "${DEPLOY_GROUP}" "${SERVICE_USER}"
    echo "Created production service user: ${SERVICE_USER}"
fi

if ! id -u "${TEAMMATE_USER}" >/dev/null 2>&1; then
    useradd --create-home --shell /bin/bash "${TEAMMATE_USER}"
    echo "Created teammate development user: ${TEAMMATE_USER}"
fi

# 2. Create directory hierarchy
mkdir -p "${BASE_DIR}/workspaces/teammate"
mkdir -p "${BASE_DIR}/review/submissions"
mkdir -p "${BASE_DIR}/staging/releases"
mkdir -p "${BASE_DIR}/production/releases"
mkdir -p "${BASE_DIR}/shared/staging"
mkdir -p "${BASE_DIR}/shared/production"

# 3. Apply isolated permissions and ownership
# Teammate workspace: owned strictly by teammate user, private
chown -R "${TEAMMATE_USER}:${TEAMMATE_USER}" "${BASE_DIR}/workspaces/teammate"
chmod 700 "${BASE_DIR}/workspaces/teammate"

# Review submissions: readable by team, writable by dev
chown -R "${TEAMMATE_USER}:${DEPLOY_GROUP}" "${BASE_DIR}/review"
chmod 775 "${BASE_DIR}/review"

# Staging & Production: owned by deploy group / service user; dev user has no access to production secrets/data
chown -R "${SERVICE_USER}:${DEPLOY_GROUP}" "${BASE_DIR}/staging" "${BASE_DIR}/production" "${BASE_DIR}/shared"
chmod 750 "${BASE_DIR}/staging"
chmod 700 "${BASE_DIR}/production"
chmod 750 "${BASE_DIR}/shared/staging"
chmod 700 "${BASE_DIR}/shared/production"

echo "=== Enightx directory structure setup completed successfully. ==="

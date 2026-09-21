# Updater v2 rewrite and release handoff

## Findings in the previous implementation

- WebSocket receive was raced against a timer; the timed-out receive remained alive while another receive was started on the same socket.
- Both startup checks and every update notification opened a modal dialog, without release deduplication.
- Every download deleted the cached ZIP/executable and used a shared temporary filename.
- ZIP contents were chosen by filename patterns, omitting possible new third-party dependencies.
- Package URLs were overwritten across releases. Checksums existed in metadata but were not enforced by the download path.
- The broadcast request schema discarded modular ZIP fields, so a push could select a different package from a normal manifest check.
- The broadcast endpoint accepted arbitrary release information without authorization.
- Install scripts overlaid files, restarted even after possible copy errors, and had no reliable install-state record.

## Replacement

`version-v2.json` is a signed envelope: base64 `payload` and base64 `signature`. Payload is a UTF-8 JSON manifest signed with RSA SHA-256 / PKCS#1 v1.5. Bootstrap installation provisions a trusted `release-public-key.pem`. A missing/untrusted signature prevents updating; TLS checks remain enabled. A checksum alone is not considered release authentication.

Protocol 2 names an immutable release and lists every file in the full Windows folder publish, with length, SHA-256 and same-host HTTPS object URL. The client compares actual installed bytes, cached objects and staged bytes against the target list. Unchanged files are copied locally; only changed/missing files are downloaded. This is file-level reuse, not binary delta compression. A changed large DLL still downloads that DLL.

Interrupted files resume with HTTP Range when the server supports it. If Range is ignored, only that file restarts. Hash/size failures stop with an error; no automatic download retry loop. Preparation is serialized with an in-process gate and cross-process cache lock. Repeated/reconnected hints never automatically install or download.

WebSockets carry hints only. Desktop refetches the signed manifest from its configured HTTPS endpoint. A single receive handles fragmented frames, with cancellation before reconnect and bounded backoff. Server polls the shared published manifest so multiple API workers see the same release; heartbeat messages prevent idle receive timeout. Notifications show a banner, never an automatic modal. Installation requires an explicit click with an empty cart and a modal dialog that blocks billing while preparing/restarting.

## A directly to D

The release declares an explicit `supportedFrom` list. A target with A listed can be prepared directly from A: the entire D file inventory is reconciled, including dependencies first introduced in B/C. B and C application packages are not required.

This is NOT automatic database migration. `databaseSchema: 1` is the baseline contract for this implementation. Schema-changing releases are blocked until a tested migration engine and database backup/recovery path are implemented. Add a source version to `supportedFrom` only after testing that exact upgrade with representative local data. The current database initializer has ad-hoc DDL; do not certify arbitrary historic schemas from a version number alone.

## Installation and limits

The Windows helper runs from the update cache, waits for graceful shutdown, verifies all staged file hashes, backs up replaced application files, records a recovery plan, copies files and restarts. A startup handshake prevents shutting down when the helper cannot start. Copy errors attempt restoration and record any restoration errors without an automatic retry/restart loop.

Business data stays under LocalApplicationData/EnightxPOS and is not part of release files. The helper does not perform database migration. Unknown files in the install directory are preserved; removed historical dependencies are not yet garbage-collected. This avoids deleting customer files, but requires checking for stale assembly conflicts when certifying an upgrade. Old cache/backups are retained; an audited retention policy is still needed.

Power loss during replacement is not a transactional filesystem update. The install-state marker blocks normal startup when detectable, and the backed-up application files plus `recovery.json` support manual recovery. If the executable cannot launch, an operator must restore the files from the backup. Restarting a process is not a complete health check of the new POS. Full end-to-end signed installation/restart with shop data remains a pilot gate.

## Bootstrap deployment: required once

Legacy 1.0.6 and earlier clients do not understand this protocol. Do not replace legacy `version.json` with the v2 envelope or broadcast a v2 package as an old ZIP update.

1. Build a new version on Windows with explicit matching Version/AssemblyVersion/FileVersion for both projects. Use a complete win-x64 self-contained folder publish, including `ApplyUpdate.ps1`.
2. Verify compiled binary versions and the generated dependency metadata. Provision the release public key into the new installation using a trusted signed installer/distribution. Keep the private signing key outside the VPS public download tree and outside Git.
3. Provision/test Windows script execution policy or sign the installer helper as required. The updater does not bypass execution policy.
4. Perform one manual/bootstrap folder installation on pilot counters, retaining the existing LocalApplicationData business database. Update shortcuts to the canonical `Enightx.Pos.Wpf.exe`; do not leave a shortcut launching a stale copied EXE.
5. Test the next signed release on that bootstrap build, including restart, recovery and database persistence before wider rollout.

Code is intentionally not auto-deployed by this branch. No signing key, trusted bootstrap package, or production release has been provisioned by this work.

## Release tooling

`infra/release_desktop.py` now only prepares a release; it has no embedded SSH password and does not edit source versions, upload files or trigger production announcements. Requires Python `cryptography` (pin in the release environment).

Example, after building and testing a folder publish:

```text
python infra/release_desktop.py --publish <folder-publish> --output <release-output> --version 1.1.1 --supported-from 1.1.0 --base-url https://posapi.eightexms.site/downloads --signing-key <private-key-path> --notes "Tested release notes"
```

Upload content-addressed `objects/` first and `releases/<version>/` next. Verify objects on the serving host, then atomically replace `/srv/enightx/downloads/version-v2.json` with the selected signed envelope. Serve this pointer without caching and objects as immutable with HTTP Range support. Never change bytes under an existing release ID. Keep the legacy feed separate until migration completes.

API settings: `ENIGHTX_RELEASE_MANIFEST` selects the local envelope path; `ENIGHTX_RELEASE_TOKEN` protects the optional compatibility POST `/api/v1/updates/broadcast`. That endpoint does not accept executable URLs; it reports the currently published release. All workers detect the pointer on the next heartbeat tick. Without the token the administrative endpoint is disabled.

## Review and validation

Tests cover duplicate/concurrent hints, numeric version normalization, direct A-to-D file reuse, new dependencies, cache reuse across service restarts, partial Range downloads, ignored Range, corruption rejection, unsafe paths, unsupported jumps/schema, signatures and mutated manifests. FastAPI tests use an isolated app with no database connection. Release-builder tests check signed complete manifests, immutable IDs, build-version mismatch and key separation.

`tests/installer-smoke.ps1` exercises actual Windows helper installation, corrupt-stage refusal and locked-file rollback on temporary fixture files. It does not start the real POS or alter the real local database.

Run:

```text
dotnet test tests/Enightx.Pos.Tests/Enightx.Pos.Tests.csproj
dotnet build apps/desktop/src/Enightx.Pos.Wpf/Enightx.Pos.Wpf.csproj
pytest tests/api/test_websocket_updates.py tests/api/test_release_builder.py
powershell -NoProfile -File tests/installer-smoke.ps1
```

Full database-connected API tests require an explicitly configured development database; do not point them at production just to satisfy a test command.

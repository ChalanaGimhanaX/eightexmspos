# Git Workflow & Team Collaboration

**Date:** 21 September 2026  
**Status:** Approved policy.

---

## 1. Branching Strategy

- `main`: Protected release branch. Only reviewed, passing, release-tagged commits are merged here. Deployed to production.
- `develop`: Integration branch. Reviewed task branches are merged into `develop` via pull requests after passing automated checks. Deployed to staging.
- `codex/<person>/<short-task>`: Individual developer feature branches branched off `develop`.
  - Examples: `codex/teammate/api-auth`, `codex/owner/desktop-billing`.
- `vX.Y.Z`: Immutable Git tags on `main` pointing to tested releases.

---

## 2. Remote & Repository Hygiene

1. **Private Remotes Only:** The central Git remote must be private and accessible only by authorized team accounts.
2. **Zero Credentials in Git:** API secrets, private keys, database passwords, and production dumps must never be committed. Local configuration files must follow `.env.example` templates.
3. **Branch Protection:**
   - PR approval required before merge to `develop` and `main`.
   - CI automated verification (unit tests, integration tests, contract parity) must pass.
   - Force pushing to `main` and `develop` is strictly prohibited.

---

## 3. Review Submission Guidelines

Every PR / submission must contain:
1. **Problem Solved:** Concise summary of the requirement or bug.
2. **Scope of Changes:** Files modified and architectural rationale.
3. **Exact Commit ID:** Git SHA identifying the submission.
4. **Verification Record:**
   - Actual test execution logs (deep verification).
   - Manual verification logs (shallow verification).
   - Unverified aspects explicitly acknowledged.
5. **Database / API / Contract Changes:** Additive vs destructive migrations, schema updates.
6. **Rollback & Recovery Plan:** Steps to roll back binary or recover local database safely.

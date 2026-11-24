# npm publication — OIDC trusted publishing

ChatToComputerMCP uses npm package **`chat-to-computer-mcp`**, with source-only local native builds. It should be published under the verified maintainer account, not by committing an npm access token to GitHub.

## Trusted Publisher configuration on npmjs.com

After **the first version exists on npm**, open its package settings → **Trusted Publisher** → **GitHub Actions** and configure the following **exact case-sensitive values**:

| npm field | Value |
|---|---|
| Organization or user | `jingzhao-l` |
| Repository | `ChatToComputerMCP` |
| Workflow filename | `publish-npm.yml` |
| Environment name | Leave **empty** (current workflow has no environment) |
| Allowed actions | Enable **Allow npm publish** (direct publishing) |

Do not enter the entire path into the workflow filename field. The file lives at `.github/workflows/publish-npm.yml`. The workflow grants `id-token: write` and runs on a **GitHub-hosted Ubuntu runner**. It uses Node 24 and npm 11+, and publishes **without** `NODE_AUTH_TOKEN`, `NPM_TOKEN` or a stored npm publish secret.

The repository and npm package are public. Keep historical private-repository notes separate from current OIDC publishing; never include credentials or untracked local files.

See [official npm Trusted Publishing](https://docs.npmjs.com/trusted-publishers/) for supported providers and limitations.

## Initial publication

The first version `0.1.0` may be published directly from an authenticated local npm CLI **after** reviewing the exact tarball and verifying the installed app. This first manual publish creates the package that npm needs for later trusted-publisher setup.

```bash
npm whoami
npm test
npm pack --dry-run --json
npm publish --access public --tag latest
npm view chat-to-computer-mcp@0.1.0 version
```

Never paste OTP or account passwords into a chat. If an interactive 2FA prompt is required, satisfy it on the computer.

### Direct-publication verification

The first release was submitted using regular interactive `npm publish`
and npm's official browser-based 2FA ceremony, not `npm stage publish`.

After accepting a package, npm may scan it for malware. During processing the
registry can temporarily show a `0.0.0-stage` placeholder while the intended
version isn't installable. If `npm stage list chat-to-computer-mcp` is empty,
that placeholder does NOT indicate an intentionally staged release.

**Do not upload the same version again** during this transition. Confirm:

```bash
npm view chat-to-computer-mcp@0.1.0 version
npm dist-tag ls chat-to-computer-mcp
```

Only when `0.1.0` is actually available and `latest` points to it is the
first release publicly installable. Subsequent stable tags use OIDC as below.
See [npm publish-time scanning](https://github.blog/changelog/2026-07-28-npm-publish-time-malware-scanning-and-dual-use-metadata/).

## Automatic subsequent stable releases

The repository workflow only publishes **stable semantic-version tags**:

```bash
# After updating package.json and verifying tests:
git tag -a v0.1.1 -m "ChatToComputerMCP v0.1.1"
git push origin v0.1.1
```

The job verifies that:

1. The tag has exactly the form `vMAJOR.MINOR.PATCH`.
2. The Git ref is on the default branch history.
3. `package.json` version equals the pushed tag without the `v`.
4. npm CLI and local installer tests pass.
5. The package content manifest contains required sources (including the CUA backend, optional worker and lockfile) and no generated build artifacts.
6. Native Mac/Windows quality gates include base schema and permission checks, native HTTP/runtime and installer regressions, Intel Mac vendor integrity and base gateway tests, native Windows ARM64 base runtime and clean installation, plus native CUA protocol and checksum-pinned real-driver read-only probes on all four architectures, and the real Chromium / Guest transport regression on the primary Mac and Windows jobs.
7. The same registry version has not already been published.

Stable publish workflows share one concurrency group and must advance the current registry latest; stale and equal versions fail closed. It then publishes to the npm `latest` dist-tag using GitHub Actions OIDC, followed by a registry read-back. The tag alone does not produce a Mac/Windows installer binary: recipients compile natively from the npm package.

**Do not reuse a published npm version.** If a verification or publishing attempt fails after npm accepted the tarball, inspect registry state before retrying. A new publication requires a new version number.

After the registry confirms the exact version and latest dist-tag, a separate
least-privilege GitHub Release job publishes the immutable tag with the matching
CHANGELOG section. A failed native or npm gate cannot publish that release. The release job rechecks npm latest before marking GitHub latest and reads back the resulting GitHub release tag.

## Optional GitHub release asset

For a versioned tarball attach `chat-to-computer-mcp-VERSION.tgz` and a SHA-256 checksum to a GitHub release. A GitHub release is **not** equivalent to an npm registry publication.

## Required environment checks and blockers

Before enabling automated tag publication, verify:

- npm Trusted Publisher matches the owner/repo/workflow exactly.
- GitHub-hosted Actions jobs must run; the 2026-11-22 private-repository billing blockage is historical, not current publication status.
- macOS and Windows test evidence is reviewable; Windows real-device tests may still be pending.
- GitHub repository visibility and npm's public package metadata are appropriate for the release; public npm packages expose their packed source even if the GitHub repository is private.
- No secrets or local credential files are in the packed tarball.

The [security policy](../SECURITY.md) describes source scanning and sensitive paths.

## Why not use self-hosted npm publishing?

npm's OIDC Trusted Publishing currently accepts GitHub-hosted runners; the existing private Mac runner cannot authenticate to npm using that trust relationship. It can run tests, but do **not** move the actual OIDC publish step to it.

## Recovery

- Workflow fails before runners start → inspect GitHub account Billing & plans.
- Workflow OIDC authentication fails → check npm Trusted Publisher configuration and `id-token: write`.
- Wrong version/tag → fix `package.json` in a new commit, then create a correct, previously unused tag.
- Existing npm version → do **not** retry a destructive overwrite. Verify `npm view`, then bump version.
- Packaging mismatch → stop before publishing; fix the pack allowlist, run tests, create a new tag.


## Historical unpublished release tag

The immutable v0.2.0 Git tag failed the Linux package-test gate before npm
publication; it was not force-moved or reused. The 0.2.1 version remained
an internal build. The next stable feature release was tagged v0.3.0,
adding native update controls. Its OIDC upload was acknowledged, but the
initial 32-second Registry read-back gate timed out while npm was processing
the package. The workflow now waits longer and checks both the version and
latest dist-tag; an accepted upload must never be repeated merely because
its publication scan has not finished.

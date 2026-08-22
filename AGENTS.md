# Awoo connector repository instructions

## Before release work

- This is an independent Git repository. Its canonical remote after the
  approved migration is `Enkianssus/awoo-connectors`, default branch `main`.
- Do not mix parent `AwooMusicBot` files into connector commits.
- Inspect `git status --short --branch`, `git remote -v`, the exact diff, and
  GitHub authentication before any remote write.
- Use only the Enkianssus GitHub and Cloudflare accounts. Never use or publish
  any other account, token, project, identifier, or branding.
- Stage named files only. Never use `git add -A`, force-push, replace signed
  assets, delete published Releases/Tags, or reuse a published version.
- Push, tag, Release, repository rename, and Cloudflare deploy all require
  explicit user authorization.

## Connector versions

- NetEase: five parts,
  `PLAYER_MAJOR.PLAYER_MINOR.PLAYER_PATCH.PLAYER_BUILD.CONNECTOR_REVISION`;
  tag example `netease-v3.1.37.205354.9`; runtime `win-x64`.
- KuGou: four parts,
  `PLAYER_MAJOR.PLAYER_MINOR.PLAYER_FEATURE.CONNECTOR_REVISION`; tag example
  `kugou-v20.0.81.5`; runtime `win-x86`. Keep the noisy full player build in
  `testedPlayerVersion`, not the connector version.
- QQ Music: three parts,
  `PLAYER_MAJOR.PLAYER_MINOR.CONNECTOR_REVISION`; tag example
  `qqmusic-v22.52.1`; runtime `win-x86`.
- Folia: three parts following the Stage API baseline; tag example
  `folia-v1.1.3`; runtime `win-x86`.
- Increase only the last part for a same-player-branch connector fix. A new
  player/API branch is a manual update boundary and starts a new revision
  sequence.
- QQ profile packs use independent SemVer tags such as
  `qqmusic-profiles-v1.2.1`; they are not connector binaries.

## Packaging and compatibility

- The existing v1 Release and `catalog.json` contract is immutable: its four
  archives (Awoo and legacy names, self-contained and framework-dependent),
  sidecars, old Tags and stable `/connectors/v1/...` URLs remain available for
  Awoo MusicBot 1.1.0-1.1.9. Do not rebuild, delete or repoint them.
- Future desktop-connector Tags use the forward v2 contract. Each new Release
  contains exactly one Awoo framework-dependent archive plus its `.sig` and
  `.sha256` sidecars (three assets total):
  `awoo-connector-{id}-{version}-{rid}-framework-dependent.zip`.
  Self-contained and `BiliNCM.*` compatibility archives are not built for v2.
- `catalog-v2.json` is the forward catalog. Its `schemaVersion` is `2`, every
  entry has `minimumCoreVersion = 1.1.10`, and its `package` object is the only
  package field. `package.deployment` is `framework-dependent`; the entry has
  no legacy, self-contained or Awoo-full-package aliases.
- Preserve `Awoo.Connector.*.exe` inside every v2 package. Already-installed
  `BiliNCM.Connector.*.exe` and self-contained connectors remain valid for old
  and new cores; the v2 cutover does not rename or remove local installations.
- Preserve `publicKeyId = bilincm-connectors-2026-01`. The v2 catalog and assets
  use the same Ed25519 signing key and immutable Release policy as v1.
- Do not manually write Release hashes, signatures, sizes, or URLs. The tag
  workflow signs the final ZIP and `github-actions[bot]` updates only
  `catalog-v2.json`; it must not modify the frozen v1 catalog.
- Do not declare a release complete until the workflow, Release assets, v2
  Catalog bot commit, public v2 proxy, Range download, signature/hash, and
  1.1.10 compatibility are verified. Old-core validation continues against
  the frozen v1 catalog and already-published Releases.
- Publish multiple connectors sequentially and wait for each v2 Catalog update;
  both workflows share the `connector-catalog` concurrency group.

## Validation and deployment

- Build `BiliNCM.Connectors.slnx` and run the tests relevant to the changed
  connector before tagging. The workflow smoke test is necessary but not a
  substitute for functional tests.
- For `cloudflare/appdownload/worker.js`, run `node --check` and
  `wrangler deploy --dry-run` before deployment.
- Deploy `appdownload` only with `ENKIANSSUS_CLOUDFLARE_API_TOKEN`; confirm the
  configured account before deployment and never print tokens.
- Keep old Releases and the former repository redirect available for old
  clients. Never recreate the old `BiliNCM-Connectors` GitHub repository name
  after the rename.

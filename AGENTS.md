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

- Each connector Release must contain Awoo and legacy archives, both
  self-contained and framework-dependent. Each ZIP requires `.sig` and
  `.sha256`, for 12 assets total.
- Preserve `Awoo.Connector.*.exe` for current cores and
  `BiliNCM.Connector.*.exe` aliases for old cores.
- Preserve all `app.enkianss.us/connectors/v1/...` URLs and
  `publicKeyId = bilincm-connectors-2026-01`.
- Do not manually write Release hashes, signatures, sizes, or URLs. The tag
  workflow signs the final ZIPs and `github-actions[bot]` updates the Catalog.
- Do not declare a release complete until the workflow, Release assets,
  Catalog bot commit, public proxy, Range download, signature/hash, and old/new
  core compatibility are verified.
- Publish multiple connectors sequentially and wait for each Catalog update;
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

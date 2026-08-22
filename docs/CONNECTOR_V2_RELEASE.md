# Connector v2 forward release contract

This document defines the forward-only connector packaging contract introduced
for Awoo MusicBot 1.1.10. It deliberately does not rewrite the published v1
contract.

## Compatibility boundary

`catalog.json` and the `/connectors/v1/` routes are a frozen compatibility
channel. They continue to describe the already-published Awoo and legacy
archives, including self-contained packages, and old cores continue to use
them. Existing Release Tags and assets are immutable and must not be deleted,
replaced or repointed.

`catalog-v2.json` is the forward channel. Its entries use
`minimumCoreVersion: "1.1.10"` because the client must understand the v2
catalog shape and the no-full-package failure behavior. The connector protocol
itself remains protocol version 1; this is a distribution-contract boundary,
not a protocol incompatibility.

The public routes that must be provided by the download Worker are:

```text
https://app.enkianss.us/connectors/v2/catalog.json
https://app.enkianss.us/connectors/v2/download/{id}/{version}/{asset}
```

The v2 route must not silently fall back to v1. Keeping the routes separate is
what prevents Awoo MusicBot 1.1.0-1.1.9 from receiving an entry it cannot
interpret.

## Catalog shape

The top-level fields remain signed-contract metadata:

```json
{
  "schemaVersion": 2,
  "repository": "Enkianssus/awoo-connectors",
  "publicKeyId": "bilincm-connectors-2026-01",
  "connectors": {
    "kugou": {
      "id": "kugou",
      "version": "20.1.42.1",
      "protocolVersion": 1,
      "minimumCoreVersion": "1.1.10",
      "package": {
        "deployment": "framework-dependent",
        "runtime": "win-x86",
        "runtimeChannel": "8.0",
        "asset": "awoo-connector-kugou-20.1.42.1-win-x86-framework-dependent.zip",
        "size": 6780000,
        "sha256": "<64 hexadecimal characters>",
        "signature": "<Ed25519 signature in base64>",
        "downloadUrl": "https://app.enkianss.us/connectors/v2/download/kugou/20.1.42.1/awoo-connector-kugou-20.1.42.1-win-x86-framework-dependent.zip"
      }
    }
  }
}
```

The v2 entry has no `asset`, `awooPackage`, `frameworkDependent` or
`awooFrameworkDependent` fields. The `package` object is the only installable
package and must be framework-dependent. `publicKeyId` stays
`bilincm-connectors-2026-01`; changing the catalog version does not rotate the
signing key.

## Release assets

For a tag such as `kugou-v20.1.42.1`, the workflow creates exactly these three
assets:

```text
awoo-connector-kugou-20.1.42.1-win-x86-framework-dependent.zip
awoo-connector-kugou-20.1.42.1-win-x86-framework-dependent.zip.sig
awoo-connector-kugou-20.1.42.1-win-x86-framework-dependent.zip.sha256
```

The ZIP contains `Awoo.Connector.<Player>.exe` and is published with
`dotnet publish --self-contained false`. It relies on the private .NET 8
runtime installed/shared by Awoo MusicBot. The final ZIP is signed after
compression; the signed bytes must never be changed or recompressed.

The workflow does not build or sign a self-contained ZIP and does not produce a
`BiliNCM.Connector.*.exe` archive alias for v2. Those files remain available in
the frozen v1 Releases for old cores. A locally installed legacy executable is
not removed by this cutover and can continue to run until the client installs a
new Awoo package.

## Release procedure

1. Build and test the connector. Keep the player-specific version format and
   tag prefix from `AGENTS.md`.
2. Push the source commit to `main`, then create a new, never-reused annotated
   connector tag (`netease-v...`, `kugou-v...`, `qqmusic-v...` or `folia-v...`).
3. The tag workflow publishes the three v2 assets, signs the final ZIP and
   runs the connector smoke test.
4. The workflow checks out `origin/main` and runs
   `scripts/update-catalog-v2.mjs`. It commits only `catalog-v2.json`; it does
   not modify the frozen `catalog.json`.
5. After the Action completes, verify the Release assets, the bot Catalog
   commit, signature/hash values, and the v2 proxy including
   `Range: bytes=0-0`. A real deployment is not complete until the current
   1.1.10 client can install the package and an old client still resolves its
   v1 package.

The generator rejects a wrong runtime, a non-framework asset name, malformed
version, SHA-256, signature or size. This prevents a large self-contained or
legacy asset from entering the v2 catalog by an operator typo.

## Client and proxy migration requirements

The main Awoo MusicBot client must fetch v2 for the 1.1.10+ update path, parse
`entry.package`, require `deployment === "framework-dependent"`, and never use a
self-contained fallback. It must continue to start, validate, upgrade and
roll back an already-installed self-contained connector. A failed private
runtime or package download must leave that active installation untouched.

The download Worker must proxy the v2 catalog from `catalog-v2.json` and allow
only the exact Awoo framework-dependent asset naming pattern for the requested
connector/version. It must keep byte-range support and immutable caching, while
leaving all `/connectors/v1/` behavior unchanged. The Worker must continue to
use the Enkianssus `awoo-connectors` repository.

## Rollback

Published v2 assets are immutable. If a connector is defective, publish a
higher connector revision on the same player branch and let the v2 catalog move
forward. If a catalog bot commit is wrong before clients observe it, revert the
catalog commit or publish the corrected higher revision; do not replace the
same-tag ZIP. The v1 catalog and old Releases remain the emergency path for
1.1.0-1.1.9 and must not be removed as part of a v2 rollback.

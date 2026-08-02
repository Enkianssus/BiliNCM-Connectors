# Awoo MusicBot Connectors

Independent native player connectors for Awoo MusicBot (formerly BiliNCM).

The repository intentionally contains no BiliNCM UI, danmaku, account,
permission, queue, HTTP API, or WebSocket server code. Each connector is built
and versioned independently so a player compatibility fix does not require a
new BiliNCM core release.

## Connectors

| Connector | Executable | Release tag |
| --- | --- | --- |
| NetEase Cloud Music | `BiliNCM.Connector.Netease.exe` | `netease-vX.Y.Z` |
| KuGou Music | `BiliNCM.Connector.Kugou.exe` | `kugou-vX.Y.Z` |
| QQ Music | `BiliNCM.Connector.QQMusic.exe` | `qqmusic-vX.Y.Z` |
| Folia | `BiliNCM.Connector.Folia.exe` | `folia-vX.Y.Z` |

All connectors use newline-delimited JSON on standard input/output. Protocol
version 1 supports `ping`, `probe`, `search`, `execute`, and `shutdown`.
Optional features are negotiated independently, so adding one does not break
older cores or connectors. NetEase `3.1.37.205354.5` advertises
`snapshot-events-v1`; a new core subscribes with `subscribe` and receives exact
snapshot event envelopes, while an older core continues to use `probe`.

QQ Music connector 22.41.4 uses exact, signed compatibility profiles for QQ Music
22.22 and 22.41. On a matching DLL hash it calls QQ's internal
`AddSongs(mode=0)` path to insert exactly one song after the current item, then
uses QQ's normal Next command. Both immediate play and guarded fallback preserve
the host playlist instead of rebuilding or appending to it. The mute/pause guard
remains active until the requested track is confirmed, so a failed native insert
does not silently fall back to the queue-breaking `/playbysongid` path.

The QQ connector advertises `snapshot-events-v1`. It combines Windows global
media-session `MediaPropertiesChanged`, `PlaybackInfoChanged`, and
`TimelinePropertiesChanged` notifications with
`SetWinEventHook(EVENT_OBJECT_NAMECHANGE)`, so the core does not run its former
350 ms QQ state poll. The guarded-next path consumes the same event stream
instead of reading the window title every 2 ms. Near a natural track ending it
uses the latest media timeline to arm one pre-mute timer for 450 ms before the
estimated end; timeline and playback events cancel and reschedule that timer.

Unknown QQ Music builds are rejected safely. The connector can submit an
anonymous compatibility report containing only the player/connector versions,
DLL SHA-256 values and analyzer results. It never uploads QQ Music binaries,
local paths, accounts, cookies, playlists or song history. A signed profile pack
can then add support without publishing a new Awoo MusicBot core release.

KuGou connector 20.0.81.3 no longer uses KuGou's queue-rebuilding immediate-play
payload. Immediate play and guarded fallback both insert exactly one track after
the current item, then send KuGou's targeted internal Next command. This keeps
the host playlist order intact and avoids the old append-and-loop behavior. On the
verified 20.0.81.27563 kugou.dll profile, a bounded anchor-history reset runs
before a new InsertNext send so the current item does not change. Unknown or
failed profiles retain the old guarded fallback and explicitly ask the user to
update the KuGou connector.

The Folia connector talks only to the local Stage HTTP/WebSocket service on
port 32107. BiliNCM passes `BILINCM_FOLIA_TOKEN` to the child process at
startup; the token is not written into the connector installation. Numeric
NetEase IDs are validated in parallel with Stage search, and exact ID results
include song, artist, album, and cover metadata.

Direct request formats are intentionally player-specific:

- NetEase treats `id=<numeric song ID>` as explicit. A bare numeric value of
  at least six digits runs exact ID lookup and keyword search in parallel;
  the exact ID wins when it exists, otherwise the keyword result is used.
- KuGou accepts a temporary numeric KuGou code with optional surrounding `#`,
  or a permanent `m.kugou.com/share/song.html?chain=...` link, `chain=...`, or
  the bare alphanumeric chain value. KuGou does not treat `id=` as a code.
- QQ Music accepts its `c6.y.qq.com/base/fcgi-bin/u?__=...` share URL,
  `u?__=...`, or a bare 12-character share code. Ordinary numeric text is not
  reinterpreted as a QQ song ID.

The NetEase connector does not start a remote debugging port and does not
restart the player with debugging flags. Its version-locked native bridge uses
CEF's in-process DevTools host to subscribe to the player's existing Redux
store. Track-change notifications therefore include the exact current song,
cover and sequential next song as events. A dedicated named-pipe long wait
forwards these changes to the connector, which then pushes them to the core;
the core no longer performs its old 350 ms state poll when this feature is
active. The connector reads the native window title only as a startup/stale-
bridge fallback: every 2 seconds while the event bridge is unavailable, and at
most once every 5 minutes while Redux events and the 15-second state heartbeat
remain healthy.

CEF compatibility uses two levels. The exact tested build is enabled directly.
An unknown patch build is tried only when both CEF public API hashes and the
CEF/Chromium major versions still match; it must then pass the existing host
layout validation and a non-persistent internal DevTools watcher probe. Any API
hash change is rejected without calling the unknown ABI.

## Versioning

The three desktop-player connectors use player-scoped versions whose final
component is the connector revision:

- NetEase `3.1.37.205354` -> connector `3.1.37.205354.5`
- KuGou `20.0.81.27563` -> connector `20.0.81.3`
- QQ Music `22.41` -> connector `22.41.4`

KuGou deliberately omits its noisy final client build component:

`KUGOU_MAJOR.KUGOU_MINOR.KUGOU_FEATURE.CONNECTOR_REVISION`

For example, KuGou `20.0.81.27563` uses connector branch `20.0.81`, so its
first connector release is `20.0.81.1` and this anchor-reset revision is
`20.0.81.3`. The noisy final KuGou build component
(`27563`) is recorded for diagnostics but does not create a new compatibility
branch. Higher connector revisions on the same player branch update
automatically; a player-version branch change is manual-only. The QQ connector
can continue to carry signed compatibility profiles for older builds such as
22.22 even when its release branch follows the newest tested build.

Folia retains the independent three-part scheme because Stage API does not
expose a desktop-player version:

- Increase `PLAYER` and reset `PATCH` to `0` only when the connector adds or
  changes its supported player-version compatibility baseline.
- Increase only `PATCH` for fixes and features that keep the same supported
  player-version baseline.
- `MAJOR` is reserved for incompatible connector protocol or packaging changes.

For Folia, the Stage API contract is treated as the player-version baseline.

## Build

```powershell
dotnet publish .\src\Netease\BiliNCM.Connector.Netease.csproj -c Release -r win-x64 --self-contained true
dotnet publish .\src\Kugou\BiliNCM.Connector.Kugou.csproj -c Release -r win-x86 --self-contained true
dotnet publish .\src\QQMusic\BiliNCM.Connector.QQMusic.csproj -c Release -r win-x86 --self-contained true
dotnet publish .\src\Folia\BiliNCM.Connector.Folia.csproj -c Release -r win-x86 --self-contained true
```

## Update catalog

The stable catalog is served through:

`https://app.enkianss.us/connectors/v1/catalog.json`

Release assets are signed with Ed25519. BiliNCM verifies both the signature and
SHA-256 digest before activating a downloaded connector, and retains the
previous version for rollback.

QQ Music compatibility profiles have a separate signed update catalog:

`https://app.enkianss.us/connectors/v1/profiles/qqmusic/catalog.json`

The core checks this catalog when launching the QQ connector. A valid newer
profile pack is installed in the background and passed to the connector through
`BILINCM_QQMUSIC_PROFILE_DIR`; signature, hash or schema failures keep the
built-in profiles active.

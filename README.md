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

QQ Music connector 1.4.0 uses exact, signed compatibility profiles for QQ Music
22.22 and 22.41. On a matching DLL hash it calls QQ's internal
`AddSongs(mode=0)` path to insert exactly one song after the current item, then
uses QQ's normal Next command. Both immediate play and guarded fallback preserve
the host playlist instead of rebuilding or appending to it. The mute/pause guard
remains active until the requested track is confirmed, so a failed native insert
does not silently fall back to the queue-breaking `/playbysongid` path.

Unknown QQ Music builds are rejected safely. The connector can submit an
anonymous compatibility report containing only the player/connector versions,
DLL SHA-256 values and analyzer results. It never uploads QQ Music binaries,
local paths, accounts, cookies, playlists or song history. A signed profile pack
can then add support without publishing a new Awoo MusicBot core release.

KuGou connector 1.5.0 no longer uses KuGou's queue-rebuilding immediate-play
payload. Immediate play and guarded fallback both insert exactly one track after
the current item, then send KuGou's targeted internal Next command. This keeps
the host playlist order intact and avoids the old append-and-loop behavior.

The Folia connector talks only to the local Stage HTTP/WebSocket service on
port 32107. BiliNCM passes `BILINCM_FOLIA_TOKEN` to the child process at
startup; the token is not written into the connector installation. Numeric
NetEase IDs are validated in parallel with Stage search, and exact ID results
include song, artist, album, and cover metadata.

Direct request formats are intentionally player-specific:

- NetEase accepts a numeric song ID or `id=<numeric song ID>`.
- KuGou accepts a temporary numeric KuGou code with optional surrounding `#`,
  or a permanent `m.kugou.com/share/song.html?chain=...` link, `chain=...`, or
  the bare alphanumeric chain value. KuGou does not treat `id=` as a code.
- QQ Music keeps its own share URL, short-code, and song-ID rules.

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

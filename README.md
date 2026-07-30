# BiliNCM Connectors

Independent native player connectors for BiliNCM.

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

QQ Music's version-locked in-process native-next implementation is compiled for
compatibility with the existing source but is disabled by the production entry
point. The normal connector uses the safer mute/pause software guard.

The Folia connector talks only to the local Stage HTTP/WebSocket service on
port 32107. BiliNCM passes `BILINCM_FOLIA_TOKEN` to the child process at
startup; the token is not written into the connector installation. Numeric
NetEase IDs are validated in parallel with Stage search, and exact ID results
include song, artist, album, and cover metadata.

## Build

```powershell
dotnet publish .\src\Netease\BiliNCM.Connector.Netease.csproj -c Release -r win-x86 --self-contained true
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

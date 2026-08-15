$ErrorActionPreference = 'Stop'

$transportPath = Join-Path $PSScriptRoot '..\src\QQMusic\QQMusicNativeNextTransport.cs'
$profilePath = Join-Path $PSScriptRoot '..\profiles\qqmusic\22.51.json'
$projectPath = Join-Path $PSScriptRoot '..\src\QQMusic\BiliNCM.Connector.QQMusic.csproj'

$transport = [IO.File]::ReadAllText(
    (Resolve-Path $transportPath),
    [Text.Encoding]::UTF8)
$profile = Get-Content -Raw -Encoding UTF8 $profilePath | ConvertFrom-Json
$project = [xml](Get-Content -Raw -Encoding UTF8 $projectPath)

$officialCallShape = '(?s)emitter\.Bytes\(0x8B, 0xCE, 0x8D, 0x97\);.*?' +
    'emitter\.Byte\(0x68\);\s*' +
    'emitter\.UInt32\(checked\(data \+ EmptyWideStringOffset\)\);\s*' +
    'emitter\.Bytes\(0x6A, 0x00, 0xB8\);.*?' +
    'emitter\.Bytes\(0xFF, 0xD0, 0x83, 0xC4, 0x08\);'

if ($transport -notmatch 'EmptyWideStringOffset = 0xD4' -or
    $transport -notmatch $officialCallShape) {
    throw 'QQ Music AddSongs must receive the non-null empty UTF-16 context and clean both stack arguments.'
}

$expectedProfile = @{
    fileVersion = '22.51'
    clientSha256 = 'A7C9F69824793B7661FBB5CEB41A9F68904F6D59EBB18D02E8265D9D5D98C16A'
    commonSha256 = 'D351295E436FFBBD8C1C2AEA1566F227271DF8390F01CBB72F06CD6362419C4D'
    singleSongPlayDispatchRva = '0x0049BDD4'
    expectedPlayDispatchBytes = 'E8 67 69 16 00'
    addSongsRva = '0x0044D570'
    songItemSize = '0xA0'
}
foreach ($entry in $expectedProfile.GetEnumerator()) {
    if ([string]$profile.($entry.Key) -cne $entry.Value) {
        throw "QQ Music 22.51 profile field '$($entry.Key)' does not match the validated image."
    }
}

if ([string]$project.Project.PropertyGroup.Version -ne '22.51.2') {
    throw 'QQ Music connector version must be 22.51.2 for this compatibility update.'
}

Write-Output 'QQMusicNativeNextTransportPolicy.Tests passed.'

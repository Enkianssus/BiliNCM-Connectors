$ErrorActionPreference = 'Stop'

$project = [IO.File]::ReadAllText(
    (Resolve-Path (Join-Path $PSScriptRoot '..\src\Kugou\BiliNCM.Connector.Kugou.csproj')),
    [Text.Encoding]::UTF8)
$adapter = [IO.File]::ReadAllText(
    (Resolve-Path (Join-Path $PSScriptRoot '..\src\Kugou\KugouPlayerAdapter.cs')),
    [Text.Encoding]::UTF8)
$catalogScript = [IO.File]::ReadAllText(
    (Resolve-Path (Join-Path $PSScriptRoot '..\scripts\update-catalog.mjs')),
    [Text.Encoding]::UTF8)
$profile = [IO.File]::ReadAllText(
    (Resolve-Path (Join-Path $PSScriptRoot '..\src\Kugou\KugouAnchorResetProfilePolicy.cs')),
    [Text.Encoding]::UTF8)

if ($project -notmatch '<Version>20\.1\.41\.1</Version>') {
    throw 'KuGou connector version must start revision 1 on player branch 20.1.41.'
}
if ($adapter -notmatch 'TestedVersion\s*=>\s*"20\.1\.41\.27870"') {
    throw 'KuGou TestedVersion must contain the tested full player build.'
}
if ($catalogScript -notmatch "playerVersionPolicy: '20\.\*'") {
    throw 'KuGou playerVersionPolicy must retain the major-20 compatibility policy.'
}
if ($catalogScript -notmatch "testedPlayerVersion: '20\.1\.41\.27870'") {
    throw 'Catalog generation must advertise the tested KuGou player build.'
}
if ($profile -notmatch '"20\.0\.81\.27563"') {
    throw 'The exact legacy KuGou anchor-reset profile must remain preserved.'
}
if ($profile -match '"20\.1\.41\.27870"') {
    throw 'Do not claim an unvalidated 20.1.41 anchor-reset profile.'
}

Write-Output 'KugouVersionPolicy.Tests passed.'

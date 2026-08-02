$ErrorActionPreference = 'Stop'

$policyPath = Join-Path $PSScriptRoot '..\src\Kugou\KugouAnchorResetProfilePolicy.cs'
$resetPath = Join-Path $PSScriptRoot '..\src\Kugou\KugouAnchorHistoryReset.cs'
$adapterPath = Join-Path $PSScriptRoot '..\src\Kugou\KugouPlayerAdapter.cs'
$policy = [IO.File]::ReadAllText((Resolve-Path $policyPath), [Text.Encoding]::UTF8)
$reset = [IO.File]::ReadAllText((Resolve-Path $resetPath), [Text.Encoding]::UTF8)
$adapter = [IO.File]::ReadAllText((Resolve-Path $adapterPath), [Text.Encoding]::UTF8)

$hasKnownProfile = $policy -match '"20\.0\.81\.27563"' -and
    $policy -match '193CEB92AC2281FCDC8A109BC533F3BC54FCCAFDA0CB1C0E61C0D140657F6132'
if (-not $hasKnownProfile) {
    throw 'The known KuGou version/hash profile is missing.'
}

$hasExactLookup = $policy -match 'StringComparison\.Ordinal' -and
    $policy -match 'StringComparison\.OrdinalIgnoreCase'
if (-not $hasExactLookup) {
    throw 'Profile lookup is not exact for version and case-insensitive for SHA-256.'
}

$updatePromptPattern = '\u8BF7\u5728\u64AD\u653E\u5668\u8BBE\u7F6E\u4E2D\u66F4\u65B0\u9177\u72D7\u8FDE\u63A5\u5668'
if ($policy -notmatch $updatePromptPattern) {
    throw 'Unknown-profile update prompt is missing.'
}

$failurePromptPattern = '\u8BF7\u66F4\u65B0\u9177\u72D7\u8FDE\u63A5\u5668'
if ($policy -notmatch 'BuildFailurePrompt' -or $policy -notmatch $failurePromptPattern) {
    throw 'Matched-profile failure update prompt is missing.'
}

$hasResetSafety = $reset -match 'KugouAnchorResetProfilePolicy\.Find\(' -and
    $reset -match 'VirtualFreeEx' -and
    $reset -match 'VirtualProtectEx' -and
    $reset -match 'IntPtr\.Size != 4' -and
    $reset -match 'ResolverDataOffset = 0x1000' -and
    $reset -match 'allocationSize: 0x2000' -and
    $reset -match 'codeProtectSize: 0x1000'
if (-not $hasResetSafety) {
    throw 'The exact-profile x86 reset safety gates are missing.'
}

$hasTimeoutLeaseSafety = $reset -match 'remoteExecutionCompleted' -and
    $reset -match 'remoteThreadStarted' -and
    $reset -match 'WaitTimeout' -and
    $reset -match '\u4E3A\u907F\u514D\u91CA\u653E\u4ECD\u5728\u6267\u884C\u7684\u4EE3\u7801\u672A\u56DE\u6536\u8FDC\u7A0B\u9875' -and
    $reset -match 'AggregateException'
if (-not $hasTimeoutLeaseSafety) {
    throw 'Remote stub timeout retention or primary-error cleanup handling is missing.'
}

$hasAdapterWiring = $adapter -match 'SendInsertNextAsync\(' -and
    $adapter -match 'tryAnchorReset: true'
if (-not $hasAdapterWiring) {
    throw 'The profile reset is not wired before a new InsertNext send.'
}

Write-Output 'KugouAnchorResetProfilePolicy.Tests passed.'

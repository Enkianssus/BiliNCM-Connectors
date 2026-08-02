$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot '..\src\Kugou\KugouPlayerAdapter.cs'
$source = Get-Content -Raw -Encoding utf8 $sourcePath
$fallbackUrl = 'http://mobilecdn.kugou.com{queryString}'
$mixedIndex = $source.IndexOf('TrySearchByMixedAsync(', [StringComparison]::Ordinal)
$guardIndex = $source.IndexOf('if (!_allowHttpSearchFallback)', [StringComparison]::Ordinal)
$fallbackIndex = $source.IndexOf($fallbackUrl, [StringComparison]::Ordinal)

if ($source -notmatch 'BILINCM_KUGOU_ALLOW_HTTP_SEARCH_FALLBACK') {
    throw 'The explicit KuGou HTTP fallback opt-in variable is missing.'
}
if ($source -notmatch 'https://gateway\.kugou\.com/v3/search/mixed') {
    throw 'The secure gateway.kugou.com mixed search endpoint is missing.'
}
if ($mixedIndex -lt 0 -or $guardIndex -lt 0 -or $fallbackIndex -lt 0) {
    throw 'The secure gateway fallback or plaintext HTTP guard is missing.'
}
if ($mixedIndex -gt $guardIndex -or $guardIndex -gt $fallbackIndex) {
    throw 'The gateway HTTPS fallback must run before the opt-in plaintext HTTP path.'
}
if ($source -notmatch 'throw new HttpRequestException\(') {
    throw 'HTTPS failure without opt-in must produce a diagnostic exception.'
}
foreach ($value in @('1', 'true', 'yes', 'on')) {
    if (-not $source.Contains(".Equals(`"$value`", StringComparison")) {
        throw "The explicit opt-in value '$value' is not recognized."
    }
}
foreach ($value in @('0', 'false', 'no', 'off')) {
    if ($source.Contains(".Equals(`"$value`", StringComparison")) {
        throw "The disabled value '$value' must not be treated as opt-in."
    }
}

Write-Output 'KugouSearchTransportPolicy.Tests passed.'

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('enightx-installer-test-' + [Guid]::NewGuid().ToString('N'))
$installer = Join-Path $PSScriptRoot '../apps/desktop/src/Enightx.Pos.Wpf/ApplyUpdate.ps1'
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
function Run-Case([string]$name, [bool]$corrupt, [bool]$locked) {
    $caseRoot = Join-Path $testRoot $name
    $target = Join-Path $caseRoot 'installed'
    $stage = Join-Path $caseRoot 'stage'
    [IO.Directory]::CreateDirectory($target) | Out-Null
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    $files = @()
    foreach ($file in @('Enightx.Pos.Wpf.exe', 'Enightx.Pos.Wpf.dll')) {
        [IO.File]::WriteAllText((Join-Path $target $file), 'original')
        [IO.File]::WriteAllText((Join-Path $stage $file), 'new-build')
        $files += @{path=$file;size=9;sha256=(Get-FileHash -LiteralPath (Join-Path $stage $file) -Algorithm SHA256).Hash}
    }
    [IO.File]::WriteAllText((Join-Path $target 'untouched.txt'), 'keep')
    if ($corrupt) { [IO.File]::WriteAllText((Join-Path $stage 'Enightx.Pos.Wpf.dll'), 'corrupted') }
    @{version='1.0.4';files=$files} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'release.json')
    $result = Join-Path $caseRoot 'result.json'
    $config = Join-Path $caseRoot 'config.json'
    @{pid=2147483000;target=$target;stage=$stage;backup=(Join-Path $caseRoot 'backup');result=$result} | ConvertTo-Json | Set-Content -LiteralPath $config
    $held = $null
    try {
        if ($locked) { $held = [IO.File]::Open((Join-Path $target 'Enightx.Pos.Wpf.dll'), 'Open', 'Read', 'Read') }
        & powershell.exe -NoProfile -File $installer -ConfigPath $config -NoRestart
    } finally { if ($held) { $held.Dispose() } }
    $state = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    $expected = if ($corrupt -or $locked) { 'original' } else { 'new-build' }
    $expectedStatus = if ($corrupt -or $locked) { 'failed' } else { 'installed' }
    if ($state.status -ne $expectedStatus) { throw "Wrong installer outcome for $name" }
    foreach ($file in @('Enightx.Pos.Wpf.exe', 'Enightx.Pos.Wpf.dll')) {
        if ([IO.File]::ReadAllText((Join-Path $target $file)) -ne $expected) { throw "Unexpected file content for $name" }
    }
    if ([IO.File]::ReadAllText((Join-Path $target 'untouched.txt')) -ne 'keep') { throw 'Unmanaged file changed' }
    Write-Output "PASS $name"
}
Run-Case 'install' $false $false
Run-Case 'corrupt-before-copy' $true $false
Run-Case 'locked-file-rollback' $false $true
Write-Output "Evidence fixtures retained at $testRoot"

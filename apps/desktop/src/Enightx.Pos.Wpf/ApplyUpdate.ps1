param([Parameter(Mandatory=$true)][string]$ConfigPath, [switch]$NoRestart)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$targetRoot = [IO.Path]::GetFullPath($config.target).TrimEnd('\') + '\'
$stageRoot = [IO.Path]::GetFullPath($config.stage).TrimEnd('\') + '\'
$backupRoot = [IO.Path]::GetFullPath($config.backup).TrimEnd('\') + '\'
if ($targetRoot -eq [IO.Path]::GetPathRoot($targetRoot) -or $targetRoot -eq $stageRoot -or $backupRoot.StartsWith($targetRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe installation roots.' }
function Safe-File([string]$root, [string]$relative) {
    if ($relative -match '(^/|\\|:|(^|/)\.\.?(/|$))') { throw 'Unsafe release path.' }
    $resolved = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (!$resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path outside installation root.' }
    # Refuse junction/symlink traversal in existing ancestors.
    $node = $resolved
    while ($node -and $node.Length -ge $root.TrimEnd('\').Length) {
        if (Test-Path -LiteralPath $node) {
            if ((Get-Item -LiteralPath $node -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not allowed.' }
        }
        $node = Split-Path -Parent $node
    }
    return $resolved
}
$manifest = Get-Content -LiteralPath (Join-Path $stageRoot 'release.json') -Raw | ConvertFrom-Json
$changes = @()
$lease = $null
try {
    $lease = [IO.File]::Open((Join-Path $targetRoot '.update-lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    if ($config.ready) { [IO.File]::WriteAllText($config.ready, 'ready') }
    $appProcess = Get-Process -Id $config.pid -ErrorAction SilentlyContinue
    if ($appProcess -and !$appProcess.WaitForExit(120000)) { throw 'Application did not close; installation cancelled.' }
    # Verify every staged file before touching any installed file.
    foreach ($file in $manifest.files) {
        $source = Safe-File $stageRoot $file.path
        $destination = Safe-File $targetRoot $file.path
        if ((Get-Item -LiteralPath $source).Length -ne $file.size -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Staged release failed verification.' }
    }
    # Back up all replaced files and persist the plan for power-loss recovery.
    foreach ($file in $manifest.files) {
        $destination = Safe-File $targetRoot $file.path
        $backup = Safe-File $backupRoot $file.path
        $existed = Test-Path -LiteralPath $destination
        if ($existed) {
            [IO.Directory]::CreateDirectory((Split-Path -Parent $backup)) | Out-Null
            Copy-Item -LiteralPath $destination -Destination $backup
        }
        $changes += [PSCustomObject]@{ path=$file.path; existed=$existed }
    }
    [IO.Directory]::CreateDirectory($backupRoot) | Out-Null
    $changes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backupRoot 'recovery.json')
    @{status='installing';version=$manifest.version;backup=$backupRoot} | ConvertTo-Json | Set-Content -LiteralPath $config.result
    foreach ($file in $manifest.files) {
        $destination = Safe-File $targetRoot $file.path
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        Copy-Item -LiteralPath (Safe-File $stageRoot $file.path) -Destination $destination -Force
    }
    @{status='installed';version=$manifest.version;backup=$backupRoot} | ConvertTo-Json | Set-Content -LiteralPath $config.result
    if (!$NoRestart) { Start-Process -FilePath (Join-Path $targetRoot 'Enightx.Pos.Wpf.exe') -WorkingDirectory $targetRoot -WindowStyle Hidden }
} catch {
    $failure = $_.Exception.Message
    $restoreErrors = @()
    foreach ($change in $changes) {
        try {
            $destination = Safe-File $targetRoot $change.path
            if ($change.existed) { Copy-Item -LiteralPath (Safe-File $backupRoot $change.path) -Destination $destination -Force }
            elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Force }
        } catch { $restoreErrors += $_.Exception.Message }
    }
    @{status='failed';error=$failure;restoreErrors=$restoreErrors;backup=$backupRoot} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $config.result
    # Do not auto-retry or start a potentially partial installation.
    exit 1
} finally { if ($lease) { $lease.Dispose() } }

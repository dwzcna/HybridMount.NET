# 完整托管构建的成功记录；不尝试跳过官方链条内的单个XAML编译目标。
if (-not ('HybridMount.BuildTools.BuildFingerprint' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'BuildFingerprint.cs') -CompilerOptions '/define:HYBRIDMOUNT_FINGERPRINT_ONLY'
}
function Get-BuildOutputFiles([string[]]$Directories) {
    $paths = foreach ($directory in $Directories) { if(Test-Path -LiteralPath $directory -PathType Leaf){[IO.Path]::GetFullPath($directory)}else{[HybridMount.BuildTools.BuildFingerprint]::Tree($directory)} }
    return @($paths | Sort-Object -Unique)
}
function Get-BuildEnvironmentHash {
    $pairs = foreach ($entry in [Environment]::GetEnvironmentVariables().GetEnumerator() | Sort-Object Key) { [string]$entry.Key + '=' + [string]$entry.Value }
    # 环境值只进入摘要，不写入记录或日志，避免泄露凭据。
    [HybridMount.BuildTools.BuildFingerprint]::Text(($pairs -join "`n"))
}
function Get-BuildPackageInputs([string]$AssetsFile) {
    $paths = [Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $AssetsFile)) { throw '还原资产不存在，不能复用构建。' }
    $assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
    $paths.Add($AssetsFile)
    foreach ($library in $assets.libraries.Values) {
        if ($library.type -ne 'package') { continue }
        $packageRoot = $null
        foreach ($folder in $assets.packageFolders.Keys) {
            $candidate = Join-Path $folder $library.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageRoot = $candidate; break }
        }
        if (-not $packageRoot) { throw "NuGet包目录不存在：$($library.path)" }
        foreach ($file in $library.files) { $paths.Add((Join-Path $packageRoot $file)) }
    }
    return $paths.ToArray()
}
function Test-BuildRecord([string]$Path, [string]$InputHash, [string[]]$OutputDirectories, [string[]]$RequiredOutputs) {
    if ([string]::IsNullOrEmpty($InputHash)) { return [pscustomobject]@{Reuse=$false;Reason='输入不可核验，保留官方构建'} }
    if (-not (Test-Path -LiteralPath $Path)) { return [pscustomobject]@{Reuse=$false;Reason='没有成功记录'} }
    try {
        $record = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        if ($record.Schema -ne 1 -or $record.InputHash -cne $InputHash -or -not $record.OutputHash) { return [pscustomobject]@{Reuse=$false;Reason='输入、属性、工具链或记录变化'} }
        foreach ($required in $RequiredOutputs) {
            if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { return [pscustomobject]@{Reuse=$false;Reason='必要输出缺失'} }
        }
        $outputs = @(Get-BuildOutputFiles $OutputDirectories)
        if ($outputs.Count -eq 0 -or [HybridMount.BuildTools.BuildFingerprint]::Files([string[]]$outputs, 'outputs-v1') -cne $record.OutputHash) { return [pscustomobject]@{Reuse=$false;Reason='输出集合或内容变化'} }
        return [pscustomobject]@{Reuse=$true;Reason='完整输入与输出内容一致';Record=$record}
    } catch { return [pscustomobject]@{Reuse=$false;Reason='记录不完整或不可核验'} }
}
function Save-BuildRecord([string]$Path, [string]$InputHash, [string[]]$OutputDirectories, [object]$Details, [string]$PerformancePath) {
    if ([string]::IsNullOrEmpty($InputHash)) { throw '输入摘要缺失，不能登记成功。' }
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $outputs = @(Get-BuildOutputFiles $OutputDirectories)
    $enumerateSeconds=$watch.Elapsed.TotalSeconds
    if ($outputs.Count -eq 0) { throw '没有输出，不能登记成功。' }
    $slowFiles=[Collections.Generic.List[object]]::new()
    if($PerformancePath){
        $outputHash=[HybridMount.BuildTools.BuildFingerprint]::Files([string[]]$outputs,'outputs-v1',[Action[string,double]]{param($file,$seconds) if($seconds -ge 0.25){$slowFiles.Add([pscustomobject]@{File=$file;Seconds=$seconds})}})
    } else {$outputHash=[HybridMount.BuildTools.BuildFingerprint]::Files([string[]]$outputs,'outputs-v1')}
    $hashSeconds=$watch.Elapsed.TotalSeconds-$enumerateSeconds
    $record = [ordered]@{Schema=1;InputHash=$InputHash;OutputHash=$outputHash;Outputs=$outputs;CompletedUtc=[DateTime]::UtcNow.ToString('O');Details=$Details}
    $json=$record | ConvertTo-Json -Depth 6
    $serializeSeconds=$watch.Elapsed.TotalSeconds-$enumerateSeconds-$hashSeconds
    [IO.Directory]::CreateDirectory((Split-Path $Path)) | Out-Null
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary,$json,[Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary,$Path,$true)
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    if($PerformancePath){[ordered]@{EnumerateSeconds=$enumerateSeconds;HashSeconds=$hashSeconds;SerializeSeconds=$serializeSeconds;WriteSeconds=$watch.Elapsed.TotalSeconds-$enumerateSeconds-$hashSeconds-$serializeSeconds;Files=$outputs.Count;SlowFiles=$slowFiles.ToArray()}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $PerformancePath -Encoding utf8}
}

function Resolve-BuildFixtures([string]$Filter,[hashtable]$Map) {
    $rank=@{None=0;Resources=1;Runtime=2;Full=3};$chosen='None'
    foreach($term in $Filter.Split('|')) {
        if($term -notmatch '^FullyQualifiedName~([A-Za-z0-9_.]+)$'){return 'Full'}
        $name=($Matches[1].Split('.'))[-1]
        if(-not $Map.ContainsKey($name)){return 'Full'}
        if($rank[$Map[$name]] -gt $rank[$chosen]){$chosen=$Map[$name]}
    }
    return $chosen
}

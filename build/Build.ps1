# 原生产物先完成再评估WinUI内容；仅复用完整成功构建，不改SDK内部XAML目标。
[CmdletBinding()]
param(
    [ValidateSet('x64','ARM64')][string]$Platform='x64',
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [ValidateSet('Packaged','Unpackaged')][string]$Deployment,
    [ValidateSet('Full','Small')][string]$RuntimeMode='Full',
    [switch]$Package,[switch]$Publish,[switch]$Test,[switch]$TestOnly,
    [switch]$RecordPerformance,[switch]$ForceBuild,
    [string]$ValidationDirectory,
    [switch]$ExperimentalThumbnails,[switch]$WithoutThumbnails,
    [ValidateSet('Native','Menus','MenusAndThumbnails')][string]$ShellProfile='MenusAndThumbnails',
    [string]$CertificateThumbprint,
    [ValidateSet('Gate','Unit','Integration','Native','Architecture','Acceptance')][string]$TestGroup='Gate',
    [string]$TestFilter='FullyQualifiedName!~FailedMountTests&FullyQualifiedName!~OrphanedMountRecoveryTests&FullyQualifiedName!~ClearRemovalTests'
)
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'BuildCache.ps1')
if($TestOnly){$Test=$true;if($Package -or $Publish){throw '-TestOnly不能同时发布。'}}
$runAcceptanceAfterGate=$Test -and $TestGroup -eq 'Gate' -and -not $PSBoundParameters.ContainsKey('TestFilter')
if(-not $PSBoundParameters.ContainsKey('TestFilter')) {
    if($TestGroup -eq 'Gate'){$TestFilter+='&FullyQualifiedName!~HybridMount.Tests.Acceptance.'}
    else{$TestFilter="FullyQualifiedName~HybridMount.Tests.$TestGroup.&$TestFilter"}
}
if(-not $Deployment){$Deployment=if($Package){'Packaged'}else{'Unpackaged'}}
if($Package -and $Deployment -eq 'Unpackaged'){throw 'MSIX请使用Packaged；目录发布使用-Publish。'}
if($ExperimentalThumbnails -and $WithoutThumbnails){throw '缩略图参数冲突。'}
if(($ExperimentalThumbnails -or $WithoutThumbnails) -and $PSBoundParameters.ContainsKey('ShellProfile')){throw '不要混用缩略图旧参数与ShellProfile。'}
if($ExperimentalThumbnails){$ShellProfile='MenusAndThumbnails'}
if($WithoutThumbnails){$ShellProfile='Menus'}
if($Test -and $Platform -eq 'ARM64' -and [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'Arm64'){throw 'ARM64测试须在ARM64主机执行；交叉编译请使用dotnet build测试工程。'}
[xml]$versionProps=Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$version=[string]$versionProps.Project.PropertyGroup[0].Version
if(-not $ValidationDirectory){$ValidationDirectory=Join-Path $repoRoot ('artifacts/validation/'+$version.Replace('.','')+'-work')}
$validation=[IO.Path]::GetFullPath($ValidationDirectory)
$validationRoot=[IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/validation'))
if(-not $validation.StartsWith($validationRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and $validation -ne $validationRoot){throw '验证记录必须保存在本仓库artifacts/validation下。'}
$runId=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6)
if($RecordPerformance){$validation=Join-Path $validation $runId}
[IO.Directory]::CreateDirectory($validation)|Out-Null
$phases=[Collections.Generic.List[object]]::new();$pendingRecords=[Collections.Generic.List[object]]::new()
$totalWatch=[Diagnostics.Stopwatch]::StartNew();$succeeded=$false
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe'|Select-Object -First 1
if(-not $msbuild){throw '需要Visual Studio 2026 C++及MSIX工具链。'}
$toolchain=[string](Get-Item -LiteralPath $msbuild).VersionInfo.FileVersion+'|'+(& dotnet --version)+'|'+$msbuild
$environmentHash=Get-BuildEnvironmentHash
# SourceLink／程序集信息依赖提交和跟踪状态；只保存摘要，远端URL不进入日志。
$gitMetadataHash=[HybridMount.BuildTools.BuildFingerprint]::Text(((& git -C $repoRoot rev-parse HEAD)+(& git -C $repoRoot ls-files --stage)+(& git -C $repoRoot config --local --get-regexp '^remote\..*\.url$')) -join "`n")
function Invoke-BuildStep([string]$Executable,[string[]]$Arguments,[string]$Name,[switch]$Capture) {
    $watch=[Diagnostics.Stopwatch]::StartNew();$status='Failed';$binlog=$null
    try {
        if($RecordPerformance -and ($Executable -eq $msbuild -or ($Executable -eq 'dotnet' -and $Arguments[0] -in 'test','build','publish'))){$binlog=Join-Path $validation ($Name+'.binlog');$Arguments+=('/bl:'+$binlog)}
        $lines=& $Executable @Arguments 2>&1
        $exitCode=$LASTEXITCODE
        $lines|Out-File -LiteralPath (Join-Path $validation ($Name+'.log')) -Encoding utf8
        if(-not $Capture){$lines|ForEach-Object{Write-Host $_}}
        if($exitCode -ne 0){throw "$Name 失败，退出码 $exitCode；日志：$validation"}
        $status='Executed';if($Capture){return ($lines -join "`n")}
    } finally {
        $watch.Stop();$phases.Add([pscustomobject]@{Name=$Name;Status=$status;Seconds=$watch.Elapsed.TotalSeconds;Binlog=$binlog})
    }
}
function Invoke-CheckStep([string]$Name,[scriptblock]$Body) {
    $watch=[Diagnostics.Stopwatch]::StartNew();$status='Failed'
    try { & $Body; $status='Executed' }
    finally { $phases.Add([pscustomobject]@{Name=$Name;Status=$status;Seconds=$watch.Elapsed.TotalSeconds}) }
}
function Get-FixtureSet {
    if(-not $TestOnly){return 'Full'}
    $map=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'TestFixtures.json') -Raw|ConvertFrom-Json -AsHashtable
    return Resolve-BuildFixtures $TestFilter $map
}
function Invoke-NativeFixtures([string]$Set) {
    if($Set -eq 'None'){return}
    if($Set -eq 'Resources') {
        Invoke-BuildStep $msbuild @('HybridMount.Ext/HybridMount.Ext.vcxproj','/m',"/p:Configuration=$Configuration","/p:Platform=$Platform",'/v:minimal','/nologo') 'native-resources'
        return
    }
    if($Set -eq 'Full') {
        $native=Join-Path $repoRoot "artifacts/native-tests/$Platform/$Configuration/"
        Invoke-BuildStep $msbuild @('HybridMount.Ext/HybridMount.Ext.vcxproj','/m',"/p:Configuration=$Configuration","/p:Platform=$Platform",'/p:HybridMountIsolatedTests=true',"/p:OutDir=$native","/p:IntDir=$($native)obj/HybridMount.Ext/",'/v:minimal','/nologo') 'native-tests'
    }
    $runtime=Join-Path $repoRoot "artifacts/native-runtime-tests/$Platform/$Configuration/"
    Invoke-BuildStep $msbuild @('HybridMount.Stock/HybridMount.Stock.vcxproj','/m',"/p:Configuration=$Configuration","/p:Platform=$Platform",'/p:HybridMountNativeTests=true',"/p:OutDir=$runtime","/p:IntDir=$($runtime)obj/HybridMount.Stock/",'/v:minimal','/nologo') 'runtime-tests'
    if($Set -eq 'Full') {
        $thumbnail=Join-Path $repoRoot "artifacts/native-thumbnail-host/$Platform/$Configuration/"
        Invoke-BuildStep $msbuild @('HybridMount.Ext/HybridMount.Ext.vcxproj','/m',"/p:Configuration=$Configuration","/p:Platform=$Platform",'/p:HybridMountShellProbeTag=d0037037',"/p:OutDir=$thumbnail","/p:IntDir=$($thumbnail)obj/HybridMount.Ext/",'/v:minimal','/nologo') 'native-thumbnail-host'
    }
}
function Get-ManagedState([string[]]$Properties,[string]$Name) {
    $project='HybridMount/HybridMount.csproj'
    # NuGet自行判断是否需要还原；不在同一构建中重复/restore。
    Invoke-BuildStep $msbuild (@($project,'/t:Restore','/v:minimal','/nologo')+$Properties) ($Name+'-restore')
    $query=Invoke-BuildStep $msbuild (@($project,'/v:quiet','/nologo','/getItem:Compile,Page,ApplicationDefinition,EmbeddedResource,Content,None,ReferencePath,Analyzer,ProjectReference','/getProperty:OutputPath,IntermediateOutputPath,ProjectAssetsFile,TargetPath,MSBuildAllProjects,RoslynTargetsPath')+$Properties) ($Name+'-evaluation') -Capture
    $match=[regex]::Match($query,'(?m)^\s*\{')
    if(-not $match.Success){throw '无法读取MSBuild实际输入集合。'}
    $evaluation=$query.Substring($match.Index)|ConvertFrom-Json -AsHashtable
    # MSBuild会返回分析器最近访问时间等非语义元数据；内容摘要已覆盖文件，访问本身不能使缓存失效。
    foreach($items in $evaluation.Items.Values){foreach($item in $items){foreach($key in @('AccessedTime','CreatedTime','ModifiedTime')){$item.Remove($key)}}}
    $normalizedEvaluation=$evaluation|ConvertTo-Json -Depth 30 -Compress
    $preprocess=Join-Path $validation ($Name+'-evaluated.xml')
    Invoke-BuildStep $msbuild (@($project,'/nologo','/v:quiet',"/pp:$preprocess")+$Properties) ($Name+'-imports')
    try {
    $hashWatch=[Diagnostics.Stopwatch]::StartNew()
    $paths=[Collections.Generic.List[string]]::new();$sourcePaths=[HybridMount.BuildTools.BuildFingerprint]::Sources($repoRoot);$paths.AddRange($sourcePaths)
    foreach($items in $evaluation.Items.Values){foreach($item in $items){if($item.FullPath -and ([string]$item.FullPath -notmatch ([regex]::Escape((Join-Path $repoRoot 'HybridMount'))+'[\\/](obj|bin)[\\/]'))){$paths.Add([string]$item.FullPath)}}}
    foreach($path in ([string]$evaluation.Properties.MSBuildAllProjects).Split(';')){if($path){$paths.Add($path)}}
    $paths.AddRange([string[]](Get-BuildPackageInputs $evaluation.Properties.ProjectAssetsFile))
    $paths.Add($msbuild)
    $dotnetRoot=Split-Path (Get-Command dotnet).Source
    foreach($pack in @('packs/Microsoft.NETCore.App.Ref','sdk/'+(& dotnet --version)+'/Roslyn')){ $folder=Join-Path $dotnetRoot $pack;if(Test-Path -LiteralPath $folder){$paths.AddRange([HybridMount.BuildTools.BuildFingerprint]::Tree($folder))} }
    $roslyn=[string]$evaluation.Properties.RoslynTargetsPath
    if($roslyn -and (Test-Path -LiteralPath $roslyn)){$paths.AddRange([HybridMount.BuildTools.BuildFingerprint]::Tree($roslyn))}
    $context=($Properties -join '|')+'|'+$toolchain+'|'+$gitMetadataHash+'|'+$environmentHash+'|'+$CertificateThumbprint+'|'+[HybridMount.BuildTools.BuildFingerprint]::Text($normalizedEvaluation)+'|'+[HybridMount.BuildTools.BuildFingerprint]::Text([IO.File]::ReadAllText($preprocess))
    $inputHash=[HybridMount.BuildTools.BuildFingerprint]::Files($paths,$context)
    $phases.Add([pscustomobject]@{Name=$Name+'-fingerprint';Status='Checked';Seconds=$hashWatch.Elapsed.TotalSeconds;Files=$paths.Count})
    $sourceHash=[HybridMount.BuildTools.BuildFingerprint]::Files($sourcePaths,'source-set-v1')
    } catch {
        Write-Warning ('构建摘要不可用，执行官方链且不登记缓存：'+$_.Exception.Message)
        $inputHash=$null;$sourceHash=$null;$context='';$paths=[Collections.Generic.List[string]]::new()
    }
    $projectRoot=Join-Path $repoRoot 'HybridMount'
    $target=[string]$evaluation.Properties.TargetPath
    $intermediate=[IO.Path]::GetFullPath((Join-Path $projectRoot $evaluation.Properties.IntermediateOutputPath))
    return [pscustomobject]@{Hash=$inputHash;Paths=$paths.ToArray();SourceHash=$sourceHash;Context=$context;Output=(Split-Path $target);Intermediate=$intermediate;Target=$target}
}
function Invoke-Managed([string]$Deploy,[ValidateSet('Build','Package','Publish')][string]$Action) {
    $name="managed-$Platform-$Configuration-$Deploy-$RuntimeMode-$Action"
    $properties=@("/p:Configuration=$Configuration","/p:Platform=$Platform","/p:HybridMountDeployment=$Deploy","/p:HybridMountRuntimeMode=$RuntimeMode","/p:HybridMountShellProfile=$ShellProfile","/p:HybridMountBuildPurpose=$Action","/p:GenerateAppxPackageOnBuild=$($Action -eq 'Package')")
    $packageDir=Join-Path $repoRoot "artifacts/packages/$version/$Platform/$Configuration/$($RuntimeMode.ToLowerInvariant())/$ShellProfile/"
    $publishDir=Join-Path $repoRoot "artifacts/publish/$version/$Platform/$Configuration/$($RuntimeMode.ToLowerInvariant())/$ShellProfile/"
    $zip=Join-Path (Split-Path $publishDir.TrimEnd('/','\')) "HybridMount.NET_${version}_${Platform}_$($RuntimeMode.ToLowerInvariant())_${ShellProfile}.zip"
    if($Action -eq 'Package'){$properties+=@("/p:AppxPackageDir=$packageDir",'/p:UapAppxPackageBuildMode=SideloadOnly','/p:AppxBundle=Never','/p:AppxPackageSigningEnabled=false')}
    if($Action -eq 'Publish'){$profile="win-$($Platform.ToLowerInvariant())"+$(if($RuntimeMode -eq 'Small'){'-small'}else{''});$properties+=@("/p:PublishProfile=$profile","/p:PublishDir=$publishDir")}
    $state=Get-ManagedState $properties $name
    $outputs=@($state.Output,$state.Intermediate)
    if($Action -eq 'Package'){$outputs+=$packageDir}
    if($Action -eq 'Publish'){$outputs+=@($publishDir,$zip)}
    $recordPath=Join-Path $repoRoot ('artifacts/build-cache/'+[HybridMount.BuildTools.BuildFingerprint]::Text(($properties -join '|')+'|'+$fixtureSet+'|'+$CertificateThumbprint)+'-success.json')
    $required=@($state.Target,(Join-Path $state.Output $(if($Deploy -eq 'Packaged'){'resources.pri'}else{'HybridMount.pri'})),(Join-Path $state.Output 'HybridMount.runtimeconfig.json'))
    $checkWatch=[Diagnostics.Stopwatch]::StartNew()
    $cacheRequired=$required+$(if($Action -eq 'Publish'){$zip})
    $reuse=Test-BuildRecord $recordPath $state.Hash $outputs $cacheRequired
    $phases.Add([pscustomobject]@{Name=$name+'-cache-check';Status='Checked';Seconds=$checkWatch.Elapsed.TotalSeconds;Reason=$reuse.Reason})
    if($ForceBuild){$reuse=[pscustomobject]@{Reuse=$false;Reason='ForceBuild'}}
    if($reuse.Reuse) {
        Write-Host "${name}：复用完整托管构建（$($reuse.Reason)）"
        $phases.Add([pscustomobject]@{Name=$name;Status='Reused';Seconds=0;Binlog=$reuse.Record.Details.Binlog})
    } else {
        Write-Host "${name}：执行官方完整链（$($reuse.Reason)）"
        if(Test-Path -LiteralPath $recordPath){Remove-Item -LiteralPath $recordPath -Force}
        if($Action -eq 'Publish' -and (Test-Path -LiteralPath $publishDir)) {
            $resolved=[IO.Path]::GetFullPath($publishDir);$allowed=[IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/publish'))+[IO.Path]::DirectorySeparatorChar
            if(-not $resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw '发布目录越界。'}
            # 仅清理本脚本的版本化生成目录，避免文件删除后残留在交付物。
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
        $target=if($Action -eq 'Publish'){'/t:Rebuild;Publish'}else{'/t:Rebuild'}
        Invoke-BuildStep $msbuild (@('HybridMount/HybridMount.csproj',$target,'/m','/v:minimal','/nologo')+$properties) $name
        foreach($file in $required){if(-not (Test-Path -LiteralPath $file)){throw "完整构建缺少必要输出：$file"}}
        if($state.Hash) {
        Invoke-CheckStep ($name+'-input-recheck') {
            $post=[HybridMount.BuildTools.BuildFingerprint]::Files($state.Paths,$state.Context)
            if($post -cne $state.Hash -or [HybridMount.BuildTools.BuildFingerprint]::Files([HybridMount.BuildTools.BuildFingerprint]::Sources($repoRoot),'source-set-v1') -cne $state.SourceHash){throw '构建期间输入发生变化，未登记成功；请重试。'}
        }
        $pendingRecords.Add([pscustomobject]@{Path=$recordPath;Hash=$state.Hash;Outputs=$outputs;Details=@{Action=$Action;Platform=$Platform;Configuration=$Configuration;Deployment=$Deploy;RuntimeMode=$RuntimeMode;ShellProfile=$ShellProfile;Fixtures=$fixtureSet;Binlog=$(if($RecordPerformance){Join-Path $validation ($name+'.binlog')}else{$null})}})
        }
    }
    if($Action -eq 'Package') {
        $packages=@(Get-ChildItem -LiteralPath $packageDir -Recurse -File -Filter '*.msix'|Where-Object{$_.Name -like '*HybridMount*'})
        if($packages.Count -ne 1){throw '没有唯一的当前MSIX。'}
        foreach($msix in $packages) {
            if($CertificateThumbprint) {
                $sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin/10.0.26100.0/x64'
                $signature=Get-AuthenticodeSignature -LiteralPath $msix.FullName
                if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint){Invoke-BuildStep (Join-Path $sdk 'signtool.exe') @('sign','/sha1',$CertificateThumbprint,'/fd','SHA256',$msix.FullName) 'sign'}
                Invoke-BuildStep (Join-Path $sdk 'signtool.exe') @('verify','/pa','/all','/v',$msix.FullName) 'signature'
            }
            Invoke-CheckStep ($name+'-package-validation') { & (Join-Path $PSScriptRoot 'Test-Package.ps1') -Path $msix.FullName -Platform $Platform -ShellProfile $ShellProfile -RuntimeMode $RuntimeMode }
            Get-FileHash -LiteralPath $msix.FullName|ConvertTo-Json|Set-Content (Join-Path $validation 'package.sha256.json') -Encoding utf8
        }
    }
    if($Action -eq 'Publish') {
        Invoke-CheckStep ($name+'-directory-validation') { & (Join-Path $PSScriptRoot 'Test-Delivery.ps1') -Path $publishDir -Platform $Platform -RuntimeMode $RuntimeMode }
        if(-not $reuse.Reuse -or -not (Test-Path -LiteralPath $zip)) {
            $temporary=$zip+'.tmp';if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}
            [IO.Compression.ZipFile]::CreateFromDirectory($publishDir,$temporary,[IO.Compression.CompressionLevel]::Optimal,$false)
            [IO.File]::Move($temporary,$zip,$true)
        }
        Invoke-CheckStep ($name+'-zip-validation') { & (Join-Path $PSScriptRoot 'Test-Delivery.ps1') -Path $zip -Platform $Platform -RuntimeMode $RuntimeMode }
        Get-FileHash -LiteralPath $zip|ConvertTo-Json|Set-Content (Join-Path $validation 'zip.sha256.json') -Encoding utf8
    }
}
# 一仓库内还原资产亦可能共享；用可取消的短等待串行化脚本，不持业务锁。
$mutex=[Threading.Mutex]::new($false,'Local\HybridMount.Build.'+[HybridMount.BuildTools.BuildFingerprint]::Text($repoRoot))
$owned=$false
Push-Location $repoRoot
try {
    try{while(-not ($owned=$mutex.WaitOne(250))) {}}catch [Threading.AbandonedMutexException]{$owned=$true}
    $fixtureSet=if($Test){Get-FixtureSet}else{'None'}
    if(-not $TestOnly -or $fixtureSet -eq 'Full') {
        # Stock已有Ext依赖；不再先单独构建一次Ext。
        Invoke-BuildStep $msbuild @('HybridMount.Stock/HybridMount.Stock.vcxproj','/m',"/p:Configuration=$Configuration","/p:Platform=$Platform",'/v:minimal','/nologo') 'native-stock-with-ext'
    }
    if(-not $TestOnly){
        if($Package){Invoke-Managed 'Packaged' 'Package'}
        if($Publish){Invoke-Managed 'Unpackaged' 'Publish'}
        if(-not $Package -and -not $Publish){Invoke-Managed $Deployment 'Build'}
    }
    if($Test) {
        Invoke-NativeFixtures $fixtureSet
        $testProperties=@("-p:Platform=$Platform","-p:HybridMountTestFixtures=$fixtureSet")
        if($fixtureSet -eq 'Runtime'){$testProperties+=('-p:HybridMountNativeResources='+(Join-Path $repoRoot "artifacts/native-runtime-tests/$Platform/$Configuration/"))}
        Invoke-BuildStep 'dotnet' (@('test','HybridMount.Tests/HybridMount.Tests.csproj','-c',$Configuration,'--logger',"trx;LogFileName=contracts-$Platform-$Configuration-$fixtureSet.trx",'--results-directory',$validation,'--filter',$TestFilter,'--logger','console;verbosity=normal','-v:minimal')+$testProperties) 'tests'
        if($runAcceptanceAfterGate){Invoke-BuildStep 'dotnet' (@('test','HybridMount.Tests/HybridMount.Tests.csproj','--no-build','-c',$Configuration,'--logger',"trx;LogFileName=acceptance-$Platform-$Configuration.trx",'--results-directory',$validation,'--filter','FullyQualifiedName~HybridMount.Tests.Acceptance.','-v:minimal')+$testProperties) 'acceptance'}
    }
    foreach($record in $pendingRecords){Invoke-CheckStep ('success-record-'+$record.Details.Action) { Save-BuildRecord $record.Path $record.Hash $record.Outputs $record.Details $(if($RecordPerformance){Join-Path $validation ('success-'+$record.Details.Action+'.json')}) }}
    $succeeded=$true
} finally {
    $totalWatch.Stop()
    [ordered]@{Version=$version;Platform=$Platform;Configuration=$Configuration;Deployment=$Deployment;RuntimeMode=$RuntimeMode;ShellProfile=$ShellProfile;TestOnly=[bool]$TestOnly;Fixtures=$fixtureSet;Succeeded=$succeeded;Seconds=$totalWatch.Elapsed.TotalSeconds;Phases=$phases.ToArray()}|ConvertTo-Json -Depth 7|Set-Content (Join-Path $validation 'build-performance.json') -Encoding utf8
    if($owned){$mutex.ReleaseMutex()};$mutex.Dispose();Pop-Location
}

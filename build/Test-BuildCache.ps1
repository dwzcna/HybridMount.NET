param([Parameter(Mandatory)][string]$WorkDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BuildCache.ps1')
$root=Join-Path ([IO.Path]::GetFullPath($WorkDirectory)) ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Value,[string]$Name){if(-not $Value){throw $Name};$checks.Add($Name)}
$inputFile=Join-Path $root 'input';[IO.File]::WriteAllText($inputFile,'input')
$out=Join-Path $root 'out';[IO.Directory]::CreateDirectory($out)|Out-Null
$file=Join-Path $out 'required.dll';[IO.File]::WriteAllText($file,'payload')
$record=Join-Path $root 'success.json';$hash=[HybridMount.BuildTools.BuildFingerprint]::Files([string[]]@($inputFile),'Full')
Check (-not (Test-BuildRecord $record $hash @($out) @($file)).Reuse) 'Missing success record rejected'
Save-BuildRecord $record $hash @($out) @{Mode='Full'}
Check ((Test-BuildRecord $record $hash @($out) @($file)).Reuse) 'Exact complete record reused'
Check (-not (Test-BuildRecord $record ([HybridMount.BuildTools.BuildFingerprint]::Files([string[]]@($inputFile),'Small')) @($out) @($file)).Reuse) 'Different build mode rejected'
$stamp=[IO.File]::GetLastWriteTimeUtc($file);[IO.File]::WriteAllText($file,'changed');[IO.File]::SetLastWriteTimeUtc($file,$stamp)
Check (-not (Test-BuildRecord $record $hash @($out) @($file)).Reuse) 'Changed output with same timestamp rejected'
[IO.File]::WriteAllText($file,'payload');[IO.File]::Delete($file)
Check (-not (Test-BuildRecord $record $hash @($out) @($file)).Reuse) 'Missing required output rejected'
[IO.File]::WriteAllText($file,'payload');$extra=Join-Path $out 'extra';[IO.File]::WriteAllText($extra,'extra')
Check (-not (Test-BuildRecord $record $hash @($out) @($file)).Reuse) 'Unexpected output rejected'
[IO.File]::Delete($extra)
Check ((Test-BuildRecord $record $hash @($out) @($file)).Reuse) 'Restored exact output accepted'
[IO.File]::WriteAllText($record,'{"Schema":1}')
Check (-not (Test-BuildRecord $record $hash @($out) @($file)).Reuse) 'Incomplete success record rejected'
[IO.File]::WriteAllText($record,'broken')
Check (-not (Test-BuildRecord $record $hash @($out) @($file)).Reuse) 'Corrupt success record rejected'
$map=@{Pure='None';Localized='Resources';Connected='Runtime'}
Check ((Resolve-BuildFixtures 'FullyQualifiedName~Pure' $map) -eq 'None') 'Pure tests need no native fixture'
Check ((Resolve-BuildFixtures 'FullyQualifiedName~Pure|FullyQualifiedName~Localized' $map) -eq 'Resources') 'Resource dependency included'
Check ((Resolve-BuildFixtures 'FullyQualifiedName~Pure|FullyQualifiedName~Connected' $map) -eq 'Runtime') 'Runtime dependency included'
Check ((Resolve-BuildFixtures 'FullyQualifiedName~Unknown' $map) -eq 'Full') 'Unknown custom filter uses full fixtures'
Check ((Resolve-BuildFixtures 'FullyQualifiedName!~Pure&FullyQualifiedName~Connected' $map) -eq 'Full') 'Complex filter uses full fixtures'
# 使用真实MSBuild执行自定义采集／成功目标，验证Set隔离和成功戳生命周期。
$fixture=Join-Path $root 'ide';[IO.Directory]::CreateDirectory((Join-Path $fixture 'obj'))|Out-Null
$fixtureSource=Join-Path $fixture 'Source.cs';[IO.File]::WriteAllText($fixtureSource,'// fixture')
$targets=[Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'HybridMount.Incremental.targets'))
$xml='<Project><PropertyGroup><BaseIntermediateOutputPath>obj\</BaseIntermediateOutputPath><TargetDir>'+[Security.SecurityElement]::Escape((Join-Path $fixture 'out\'))+'</TargetDir><HybridMountDeployment>Unpackaged</HybridMountDeployment></PropertyGroup><ItemGroup><Compile Include="Source.cs"/></ItemGroup><Import Project="'+$targets+'"/><Target Name="Build"/></Project>'
$fixtureProject=Join-Path $fixture 'Fixture.proj';[IO.File]::WriteAllText($fixtureProject,$xml)
$items=& dotnet msbuild $fixtureProject '/t:Build;CollectHybridMountUpToDateItems' /nologo /v:quiet /getItem:UpToDateCheckInput,UpToDateCheckOutput 2>&1
if($LASTEXITCODE -ne 0){throw ($items -join "`n")}
$state=($items -join "`n")|ConvertFrom-Json
$completion=@($state.Items.UpToDateCheckOutput|Where-Object Set -eq 'HybridMountComplete')
$payload=@($state.Items.UpToDateCheckOutput|Where-Object Set -eq 'HybridMountPayload')
Check ($completion.Count -eq 1 -and (Test-Path -LiteralPath $completion[0].FullPath)) 'Official successful Build writes one completion stamp'
Check (@($state.Items.UpToDateCheckInput|Where-Object Set -ne 'HybridMountComplete').Count -eq 0) 'Custom source inputs belong to completion set'
Check ($payload.Count -ge 10 -and @($state.Items.UpToDateCheckInput|Where-Object Set -eq 'HybridMountPayload').Count -eq 0) 'Stable payloads use independent existence set'
Check (([IO.File]::GetLastWriteTimeUtc($completion[0].FullPath)) -ge ([IO.File]::GetLastWriteTimeUtc($fixtureSource))) 'Completion follows processing of source input'
[IO.File]::Delete($completion[0].FullPath)
$failedXml=$xml.Replace('<Target Name="Build"/>','<Target Name="Build"><Error Text="Expected fixture failure"/></Target>')
[IO.File]::WriteAllText($fixtureProject,$failedXml)
$null=& dotnet msbuild $fixtureProject /t:Build /nologo /v:quiet 2>&1
$expectedFailure=$LASTEXITCODE -ne 0
Check ($expectedFailure -and -not(Test-Path -LiteralPath $completion[0].FullPath)) 'Failed Build does not create a completion stamp'
[ordered]@{Passed=$checks.Count;Checks=$checks.ToArray()}|ConvertTo-Json -Depth 3

# 同版双架构Store候选；正式门禁只检查、不上传，不创建或导出签名私钥。
[CmdletBinding()]
param([switch]$Submission,[switch]$CheckOnly,[switch]$ForceBuild,[string]$MaterialsPath)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'StorePolicy.ps1')
Assert-StoreConfiguration 'Release' 'Packaged' 'Full' ''
[xml]$props=Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
$version=[string]$props.Project.PropertyGroup[0].Version
$identity=[string]$props.Project.PropertyGroup[0].HybridMountPackageName
$publisher=[string]$props.Project.PropertyGroup[0].HybridMountPublisher
if(-not $MaterialsPath){$MaterialsPath=Join-Path $PSScriptRoot 'StoreSubmission.json'}
$materials=Get-Content -LiteralPath $MaterialsPath -Raw|ConvertFrom-Json -AsHashtable
$blockers=@(Get-StoreSubmissionBlockers $version $materials $repo)
$mode=if($Submission){'submission'}else{'preparation'}
$validation=Join-Path $repo ('artifacts/validation/'+$version.Replace('.','')+'-work/store-'+$mode)
[IO.Directory]::CreateDirectory($validation)|Out-Null
$report=[ordered]@{Version=$version;Identity=$identity;Publisher=$publisher;Channel='Store';Mode=$mode;ReadyToSubmit=($blockers.Count -eq 0);Blockers=$blockers;Uploaded=$false}
$report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $validation 'preflight.json') -Encoding utf8
if($Submission -and $blockers.Count){throw ('正式提交门禁拒绝：'+($blockers -join '；'))}
if($CheckOnly){$report|ConvertTo-Json -Depth 5;return}
$run=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6)
$output=Join-Path $repo "artifacts/store/$version/$mode/$run"
$inputDirectory=Join-Path $output 'bundle-input'
[IO.Directory]::CreateDirectory($inputDirectory)|Out-Null
foreach($arch in @('x64','ARM64')) {
    & (Join-Path $PSScriptRoot 'Build.ps1') -Platform $arch -Configuration Release -Deployment Packaged -RuntimeMode Full -DistributionChannel Store -Package -ForceBuild:$ForceBuild -RecordPerformance -ValidationDirectory $validation
    $folder=Join-Path $repo "artifacts/packages/$version/Store/$identity/$arch/Release/full/MenusAndThumbnails"
    $packages=@(Get-ChildItem -LiteralPath $folder -Recurse -File -Filter '*.msix')
    if($packages.Count -ne 1){throw '没有唯一的当前架构Store包。'}
    Copy-Item -LiteralPath $packages[0].FullName -Destination (Join-Path $inputDirectory "HybridMount.NET_${version}_${arch}.msix")
}
$makeappx=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin/10.0.26100.0/x64/makeappx.exe'
$bundle=Join-Path $output "HybridMount.NET_${version}_x64_arm64.msixbundle"
& $makeappx bundle /d $inputDirectory /p $bundle /bv $version /o *> (Join-Path $validation 'makeappx-bundle.log')
if($LASTEXITCODE -ne 0){throw '双架构bundle生成失败。'}
$uploadContent=Join-Path $output 'upload-content';[IO.Directory]::CreateDirectory($uploadContent)|Out-Null
Copy-Item -LiteralPath $bundle -Destination $uploadContent
$upload=Join-Path $output "HybridMount.NET_${version}_x64_arm64.msixupload"
[IO.Compression.ZipFile]::CreateFromDirectory($uploadContent,$upload,[IO.Compression.CompressionLevel]::Optimal,$false)
& (Join-Path $PSScriptRoot 'Test-Store.ps1') -Path $upload
$report['Bundle']=$bundle;$report['UploadContainer']=$upload;$report['SHA256']=(Get-FileHash -LiteralPath $upload).Hash
$report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $validation 'candidate.json') -Encoding utf8
Write-Host "Store $mode 候选已检查：$upload；未上传。正式材料缺项：$($blockers.Count)。"

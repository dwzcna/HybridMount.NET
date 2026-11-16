# 检查实际上传容器、bundle身份及两个架构包；不把本检查称为WACK或Store认证。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$props=Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
$version=[string]$props.Project.PropertyGroup[0].Version
$name=[string]$props.Project.PropertyGroup[0].HybridMountPackageName
$publisher=[string]$props.Project.PropertyGroup[0].HybridMountPublisher
$work=Join-Path $repo ('artifacts/validation/'+$version.Replace('.','')+'-work/store-content/'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work)|Out-Null
$container=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
try {
    if($container.Entries.Count -ne 1 -or $container.Entries[0].Name -notlike '*.msixbundle'){throw '上传容器必须且只能包含当前双架构bundle。'}
    $bundle=Join-Path $work 'candidate.msixbundle';[IO.Compression.ZipFileExtensions]::ExtractToFile($container.Entries[0],$bundle)
} finally {$container.Dispose()}
$archive=[IO.Compression.ZipFile]::OpenRead($bundle)
try {
    $entry=$archive.GetEntry('AppxMetadata/AppxBundleManifest.xml');if(-not $entry){throw 'bundle清单缺失。'}
    $reader=[IO.StreamReader]::new($entry.Open());try{[xml]$manifest=$reader.ReadToEnd()}finally{$reader.Dispose()}
    if($manifest.Bundle.Identity.Name -cne $name -or $manifest.Bundle.Identity.Publisher -cne $publisher -or $manifest.Bundle.Identity.Version -cne $version){throw 'bundle身份或版本不一致。'}
    $applications=@($manifest.Bundle.Packages.Package|Where-Object{$_.Type -eq 'application'})
    if($applications.Count -ne 2 -or @($applications|Where-Object{$_.Architecture -eq 'x64'}).Count -ne 1 -or @($applications|Where-Object{$_.Architecture -eq 'arm64'}).Count -ne 1){throw 'bundle必须有且仅有x64和ARM64应用包。'}
    foreach($app in $applications) {
        if($app.Version -cne $version){throw '架构包版本不一致。'}
        $entry=$archive.GetEntry($app.FileName);if(-not $entry -or $entry.Name -ne $app.FileName){throw '架构包缺失或路径不合法。'}
        $package=Join-Path $work $entry.Name;[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$package)
        & (Join-Path $PSScriptRoot 'Test-Package.ps1') -Path $package -Platform $app.Architecture -RuntimeMode Full -DistributionChannel Store
    }
    if($archive.GetEntry('AppxSignature.p7x')){throw 'Store候选bundle不得使用侧载签名。'}
} finally {$archive.Dispose()}
Write-Host 'Store上传容器及双架构Full包检查通过；WACK和Store重签另行验收。'

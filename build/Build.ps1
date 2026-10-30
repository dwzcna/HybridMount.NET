# 先构建原生扩展，再评估 WinUI 的内容；相同架构输出按顺序写入。
[CmdletBinding()]
param(
    [ValidateSet('x64','ARM64')][string]$Platform = 'x64',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('Packaged','Unpackaged')][string]$Deployment,
    [switch]$Package,
    [switch]$Test,
    [string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
if (-not $Deployment) { $Deployment = if ($Package) { 'Packaged' } else { 'Unpackaged' } }
if ($Package -and $Deployment -eq 'Unpackaged') { throw '非打包模式不能生成 MSIX；请使用 -Deployment Packaged。' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$validation = Join-Path $repoRoot 'artifacts\validation'
[IO.Directory]::CreateDirectory($validation) | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '需要 Visual Studio 2026 C++ 与 MSIX 工具链。' }
function Invoke-BuildStep([string]$executable, [string[]]$arguments, [string]$name) {
    & $executable @arguments 2>&1 | Tee-Object -FilePath (Join-Path $validation "$name.log")
    if ($LASTEXITCODE -ne 0) { throw "$name 失败，退出码 $LASTEXITCODE" }
}
Push-Location $repoRoot
try {
    Invoke-BuildStep $msbuild @('HybridMount.Ext\HybridMount.Ext.vcxproj','/m',"/p:Configuration=$Configuration","/p:Platform=$Platform",'/v:minimal','/nologo') "native-$Platform-$Configuration"
    $packageDirectory = Join-Path $repoRoot "artifacts\packages\$Platform\"
    $managedArguments = @('HybridMount\HybridMount.csproj','/restore','/m',"/p:Configuration=$Configuration","/p:Platform=$Platform",'/v:minimal','/nologo',"/p:HybridMountDeployment=$Deployment","/p:GenerateAppxPackageOnBuild=$($Package.IsPresent)")
    if ($Package) { $managedArguments += @("/p:AppxPackageDir=$packageDirectory",'/p:UapAppxPackageBuildMode=SideloadOnly','/p:AppxBundle=Never','/p:AppxPackageSigningEnabled=false') }
    Invoke-BuildStep $msbuild $managedArguments "managed-$Platform-$Configuration-$Deployment"
    if ($Test) {
        if ($Platform -eq 'ARM64' -and [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'Arm64') { throw 'ARM64 测试必须在 ARM64 主机执行；请仅交叉构建。' }
        # 原生端到端测试使用独立 DLL／管道，不占用用户正在运行的产品管道。
        $testNative=Join-Path $repoRoot "artifacts\native-tests\$Platform\$Configuration\"
        $testNativeObjects=Join-Path $testNative 'obj\'
        Invoke-BuildStep $msbuild @('HybridMount.Ext\HybridMount.Ext.vcxproj','/m',"/p:Configuration=$Configuration","/p:Platform=$Platform",'/p:HybridMountIsolatedTests=true',"/p:OutDir=$testNative","/p:IntDir=$testNativeObjects",'/v:minimal','/nologo') "native-tests-$Platform-$Configuration"
        Invoke-BuildStep 'dotnet' @('test','HybridMount.Tests\HybridMount.Tests.csproj','-c',$Configuration,"-p:Platform=$Platform",'--logger',"trx;LogFileName=contracts-$Platform-$Configuration.trx",'--results-directory',$validation,'-v:minimal') "tests-$Platform-$Configuration"
    }
    if ($Package) {
        [xml]$versionProps=Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
        $packageVersion=[string]$versionProps.Project.PropertyGroup[0].Version
        if(([version]$packageVersion).Revision -lt 0) { $packageVersion += '.0' }
        $packagePattern='*_'+$packageVersion+'_'+$Platform.ToLowerInvariant()+'.msix'
        $packages = @(Get-ChildItem -LiteralPath $packageDirectory -Filter $packagePattern -Recurse -File)
        if (-not $packages.Count) { throw '构建未生成 MSIX。' }
        foreach ($msix in $packages) {
            if ($CertificateThumbprint) {
                $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64'
                Invoke-BuildStep (Join-Path $sdkBin 'signtool.exe') @('sign','/sha1',$CertificateThumbprint,'/fd','SHA256',$msix.FullName) "sign-$Platform"
                Invoke-BuildStep (Join-Path $sdkBin 'signtool.exe') @('verify','/pa','/all','/v',$msix.FullName) "signature-$Platform"
            }
            & (Join-Path $PSScriptRoot 'Test-Package.ps1') -Path $msix.FullName -Platform $Platform
            Get-FileHash -LiteralPath $msix.FullName -Algorithm SHA256 | Format-List | Out-File (Join-Path $validation "package-$Platform.sha256.txt") -Encoding utf8
        }
    }
} finally { Pop-Location }

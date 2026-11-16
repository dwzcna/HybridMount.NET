param([Parameter(Mandatory)][string]$WorkDirectory)
$ErrorActionPreference='Stop'
$WorkDirectory=[IO.Path]::GetFullPath($WorkDirectory)
. (Join-Path $PSScriptRoot 'StorePolicy.ps1')
$passed=0
foreach($version in @('0.0.25.0','0.0.25.1','1.0.0.1','1.0','65536.0.0.0','bad')) {
    $issues=@(Get-StoreSubmissionBlockers $version @{} $WorkDirectory)
    if(-not @($issues|Where-Object{$_ -like 'Store正式版本*'}).Count){throw '非法正式版本被接纳：'+$version};$passed++
}
$area=Join-Path $WorkDirectory ([Guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($area)|Out-Null
[IO.File]::WriteAllText((Join-Path $area 'actual.png'),'fixture');[IO.File]::WriteAllText((Join-Path $area 'wack.xml'),'<fixture/>')
$ready=@{PrivacyPolicyUrl='https://privacy.publisher.net/policy';SupportContact='https://support.publisher.net';ReviewServiceReady=$true;ReviewAccountDeliveredPrivately=$true;AgeRatingCompleted=$true;StoreResigningValidated=$true;CleanInstallAndUninstallValidated=$true;DeviceAcceptanceCompleted=$true;ScreenshotsDirectory=$area;WackReport=(Join-Path $area 'wack.xml')}
if(@(Get-StoreSubmissionBlockers '1.0.0.0' $ready $WorkDirectory).Count){throw '完整夹具被错误拒绝。'};$passed++
foreach($key in $ready.Keys) {
    $missing=$ready.Clone();$missing[$key]=$null
    if(-not @(Get-StoreSubmissionBlockers '1.0.0.0' $missing $WorkDirectory).Count){throw '缺项未被拒绝：'+$key};$passed++
}
foreach($url in @('http://privacy.publisher.net','https://example.com','https://localhost','https://user:secret@privacy.publisher.net','bad')){if(Test-PublicHttpsUrl $url){throw '无效公开地址被接纳。'};$passed++}
foreach($tuple in @(@('Debug','Packaged','Full',''),@('Release','Unpackaged','Full',''),@('Release','Packaged','Small',''),@('Release','Packaged','Full','oldcert'))){$thrown=$false;try{Assert-StoreConfiguration @tuple}catch{$thrown=$true};if(-not $thrown){throw '非法Store构建组合被接纳。'};$passed++}
Assert-StoreConfiguration 'Release' 'Packaged' 'Full' ''; $passed++
@{Passed=$passed}|ConvertTo-Json -Compress

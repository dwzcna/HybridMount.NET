# 只验证发行条件；不访问Partner Center、不保存审核凭据、不把准备版当正式提交。
function Assert-StoreConfiguration([string]$Configuration,[string]$Deployment,[string]$RuntimeMode,[string]$CertificateThumbprint) {
    if($Configuration -cne 'Release' -or $Deployment -cne 'Packaged' -or $RuntimeMode -cne 'Full' -or $CertificateThumbprint){throw 'Store必须为Release／Packaged／Full，且与侧载签名分离。'}
}
function Test-PublicHttpsUrl([string]$Value) {
    $uri=$null
    if(-not [Uri]::TryCreate($Value,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.IsLoopback){return $false}
    return $uri.Host -notmatch '(^|\.)(example\.(com|org|net)|invalid|localhost|test)$' -and $uri.Host.Contains('.')
}
function Get-StoreSubmissionBlockers([string]$Version,[hashtable]$Materials,[string]$RepositoryRoot) {
    $issues=[Collections.Generic.List[string]]::new();$parsed=$null
    if(-not [version]::TryParse($Version,[ref]$parsed) -or $parsed.Major -le 0 -or $parsed.Revision -ne 0 -or $parsed.Major -gt 65535 -or $parsed.Minor -gt 65535 -or $parsed.Build -gt 65535){$issues.Add('Store正式版本必须为四段、首段非0、第四段0，且每段不超过65535。')}
    if(-not(Test-PublicHttpsUrl $Materials.PrivacyPolicyUrl)){$issues.Add('缺少真实公开的HTTPS隐私政策网址。')}
    if(-not $Materials.SupportContact -or $Materials.SupportContact -match 'example\.|placeholder|待提供'){$issues.Add('缺少真实支持方式。')}
    foreach($item in @('ReviewServiceReady','ReviewAccountDeliveredPrivately','AgeRatingCompleted','StoreResigningValidated','CleanInstallAndUninstallValidated','DeviceAcceptanceCompleted')) {
        if($Materials[$item] -isnot [bool] -or -not $Materials[$item]){$issues.Add('未完成提交材料或验收：'+$item)}
    }
    foreach($item in @('ScreenshotsDirectory','WackReport')) {
        $value=$Materials[$item]
        if(-not $value){$issues.Add('缺少提交证据：'+$item);continue}
        $path=if([IO.Path]::IsPathFullyQualified($value)){[IO.Path]::GetFullPath($value)}else{[IO.Path]::GetFullPath((Join-Path $RepositoryRoot $value))}
        if(-not(Test-Path -LiteralPath $path)){$issues.Add('提交证据不存在：'+$item);continue}
        if($item -eq 'ScreenshotsDirectory' -and (-not(Test-Path -LiteralPath $path -PathType Container) -or -not @(Get-ChildItem -LiteralPath $path -File|Where-Object{$_.Extension -in '.png','.jpg','.jpeg'}).Count)){$issues.Add('缺少真实运行截图。')}
        if($item -eq 'WackReport' -and -not(Test-Path -LiteralPath $path -PathType Leaf)){$issues.Add('WACK报告必须为实际文件。')}
    }
    return $issues.ToArray()
}

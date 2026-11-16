# 许可文稿使用仓库单一源；交付核对实际字节，不能只检查文件名存在。
function Assert-LicensePayload {
    param([Parameter(Mandatory)][scriptblock]$ReadBytes)
    $licenseRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $thirdPartyRoot=Join-Path $licenseRoot 'HybridMount/ThirdParty'
    $files=@(Get-Item -LiteralPath (Join-Path $licenseRoot 'LICENSE'))+@(Get-ChildItem -LiteralPath $thirdPartyRoot -Recurse -File -Filter '*.txt')
    foreach($file in $files){
        $name=if($file.FullName -eq (Join-Path $licenseRoot 'LICENSE')){'LICENSE'}else{'ThirdParty/'+$file.FullName.Substring($thirdPartyRoot.Length+1).Replace('\','/')}
        [byte[]]$actual=& $ReadBytes $name
        if($null -eq $actual -or $actual.Length -eq 0){throw "交付物缺少许可证或声明：$name"}
        $expected=[IO.File]::ReadAllBytes($file.FullName)
        $hash=[Security.Cryptography.SHA256]::Create()
        try{if([BitConverter]::ToString($hash.ComputeHash($actual)) -cne [BitConverter]::ToString($hash.ComputeHash($expected))){throw "交付物许可原文不一致：$name"}}
        finally{$hash.Dispose()}
    }
    Write-Host "许可载荷检查通过：$($files.Count)份许可文稿及清单。"
}

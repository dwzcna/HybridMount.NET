    # 从托管 PE 的资源目录读取标准 .resources；不加载程序集，不执行目标架构代码。
    function Read-ManagedStrings([string]$imagePath,[string]$resourceName,[string]$culture,[string]$assemblyName) {
        $imageStream=[IO.File]::OpenRead($imagePath)
        $pe=[System.Reflection.PortableExecutable.PEReader]::new($imageStream)
        try {
            $metadata=[System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $identity=$metadata.GetAssemblyDefinition()
            if($metadata.GetString($identity.Name) -ne $assemblyName -or $metadata.GetString($identity.Culture) -ne $culture){throw "托管语言资源身份不正确：$culture"}
            $matches=@($metadata.ManifestResources | Where-Object {$metadata.GetString($metadata.GetManifestResource($_).Name) -eq $resourceName})
            if($matches.Count -ne 1){throw "托管资源缺失或重复：$resourceName"}
            $resource=$metadata.GetManifestResource($matches[0])
            if(-not $resource.Implementation.IsNil){throw "托管资源不是内嵌数据：$resourceName"}
            $address=$pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress+[int]$resource.Offset
            $reader=$pe.GetSectionData($address).GetReader();$length=$reader.ReadInt32()
            if($length -lt 0 -or $length -gt 16MB){throw '托管资源长度超限。'}
            $payload=$reader.ReadBytes($length);$buffer=[IO.MemoryStream]::new($payload,$false)
            $strings=[System.Resources.ResourceReader]::new($buffer)
            try {
                $values=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
                $items=$strings.GetEnumerator()
                while($items.MoveNext()) {
                    if($items.Key -isnot [string] -or $items.Value -isnot [string]){throw '托管文案必须为字符串。'}
                    $values.Add([string]$items.Key,[string]$items.Value)
                }
                return ,$values
            } finally {$strings.Dispose();$buffer.Dispose()}
        } finally {$pe.Dispose();$imageStream.Dispose()}
    }

# 托管投影可以是AnyCPU；I386仅在纯IL且不要求／偏好32位时才适用于两种64位交付。
function Assert-ManagedImageArchitecture([byte[]]$Bytes,[string]$Platform) {
    $stream=[IO.MemoryStream]::new($Bytes,$false)
    $pe=[Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $cor=$pe.PEHeaders.CorHeader
        if(-not $cor){throw '预期的托管投影没有CLR头。'}
        $machine=[int]$pe.PEHeaders.CoffHeader.Machine
        $expected=if($Platform -eq 'ARM64'){0xaa64}else{0x8664}
        if($machine -eq $expected){return}
        $flags=[int]$cor.Flags
        if($machine -eq 0x14c -and ($flags -band 1) -ne 0 -and ($flags -band 0x20002) -eq 0){return}
        throw "托管投影架构不适用于$Platform。"
    } finally {$pe.Dispose();$stream.Dispose()}
}
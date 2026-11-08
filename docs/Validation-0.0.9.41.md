# HybridMount 0.0.9.41 验收记录

基线1bc15ed，.40已本地提交；仅三个工程，Debug／Release及x64／ARM64，代码与自动验证预算3—5小时。不推送，不操作正式挂载或真实远端，不重启Explorer，不继续.33停止专项。

## 证据与实现

00941-probe2（4/4）使用隔离NTFS与回环DAV，采样标记前、标记后、扫描、读取及重连。冷零字节／非零占位Pin=3、InSync=1、Modified=0时，StorageProviderState=5；读取完整后变9。已下载／固定占位设置Pin=3立即为9。SyncTransferStatus的0x200在干净文件也存在，不能猜测为本程序上传挂起。

因此冷文件只发布排除图标、阻止上传，并暂缓原生EXCLUDED；保留远端枚举及按需读取，不主动水合、不转换普通文件或伪造InSync。日志保存NativeDeferred、原Pin和本功能实际拥有的OwnedPin，中断恢复只修改身份相同且意图仍匹配的文件。零字节依据PARTIAL元数据，不仅按长度判断完整。正常读取后补原生标记，解除时恢复原意图。原生菜单查询只读自定义排除元数据；此属性在GPS_FASTPROPERTIESONLY下为空，使用特定元数据的GPS_DEFAULT读回，经测试无正文请求。

## 验证记录

- 00941-minimal首次原生编译发现IPersistSerializedPropStorage参数类型问题，修正显式ABI转换；00941-minimal2通过，首批相关89/89。
- 00941-debug-regression首轮1311通过／5失败：恢复测试未等待生产30秒退避、初始排除后固定本应跳过、一次水合完成与扫描采样时序、原生快速属性读漏排除。分别修正测试边界和实际菜单读取；原始日志保留。
- 最终00941-final-debug-regression：1316/1316通过，0跳过。Release首轮环境未先构建同版本解码宿主，1300通过／16失败；00941-release-ready为宿主完成后的复测，最终1316/1316通过，0失败／0跳过；无源码变更后重跑，不把首次失败抹除。
- 00941-delivery-debug／unpackaged／x64／arm64均通过；双架构签名及包内容核验通过，正式Menus配置及缩略图门控保持；ARM64测试仅交叉编译通过。
- Compile主232／Page11、测试324，无重复；00941-main-items.json、00941-test-items.json。

- 本轮1286个新增32位测试CLSID，均为已删除的当前用户GUID测试目录、测试图标、零原生／NameSpace引用。00941-regression-clsid-backup.json完整备份；cleanup／postcheck确认1286项删除后剩余0，00941-registry-final.json作为下一轮基线。未处理历史未知项。首次清理预审因审计沿用旧LASTEXITCODE被拒绝（没有删除），独立只读核对确认审计真实通过后，重新获准完成备份和精确删除。

## 待测与边界

Explorer真实图标／默认列、系统菜单与固定意图交互、生产初始排除、正式MSIX安装升级、最终非打包WinUI启动及ARM64实机待测。自动验证不会替代目视。冷文件灰色排除图标与云图标共存是保留的仅联机状态；完整后才使用原生EXCLUDED，不承诺冷占位同时拥有原生状态9。正式匹配项仍可枚举和读取，未因本轮反馈扩大为隐藏或删除。

签名包：artifacts/packages/x64/HybridMount_0.0.9.41_x64_Test/HybridMount_0.0.9.41_x64.msix及对应ARM64目录；本轮未安装最终包。

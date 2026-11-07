# 0.0.9.32：Shell 缩略图与原生状态分析

日期：2026-11-07。按用户要求，由 GPT 6.1 SOL 子代理只读核对官方资料，主执行者完成隔离验证。本文件区分接口事实、测试结果和待证实假设，不把方案或直接调用处理器的成功当作 Explorer 自动接管成功。

## 官方接口结论

- [CFAPI 门户](https://learn.microsoft.com/en-us/windows/win32/cfapi/cloud-files-api-portal)、[CF_CALLBACK_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_callback_type) 和 [CF_OPERATION_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_operation_type) 没有专用的缩略图字节回调／传输操作。文件水合传输必须返回实际文件对应范围的正文，不能用小 PNG 替代原文件内容。
- 受支持的小图入口是 [desktop3:ThumbnailProviderHandler](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop3-thumbnailproviderhandler) 绑定 COM 类，再由 [IThumbnailProvider::GetThumbnail](https://learn.microsoft.com/en-us/windows/win32/api/thumbcache/nf-thumbcache-ithumbnailprovider-getthumbnail) 返回 HBITMAP。现有严格范围读取、独立解码和 PNG 缓存可以复用，但注册、激活、归属检查、IPC 和缓存刷新都必须实际验证。不能根据 OneDrive 的外观推断其私有实现。
- [IThumbnailCachePrimer](https://learn.microsoft.com/en-us/windows/win32/api/thumbcache/nn-thumbcache-ithumbnailcacheprimer) 标注不支持，不作为产品写缓存方案。SIIGBF_THUMBNAILONLY 为 8；ICONONLY 为 4，成功返回图标不证明缩略图有效。
- [原生水合策略](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_hydration_policy_primary) 文档明确 Partial 当前不支持；Progressive 可能在用户句柄关闭前继续后台读取，Full 会令小读取等待完整水合。此次隔离对照证明仅改为后二者不足以修复当前 Shell 图像选择，因此生产根策略保持原值，未自动迁移。
- [AllowPinning](https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootinfo.allowpinning) 默认值为 true。不能把没有显式赋值当作系统原生固定／释放菜单缺失的已确认根因。
- 缩略图与预览处理器是不同接口。[缩略图默认进程隔离](https://learn.microsoft.com/en-us/windows/win32/shell/thumbnail-providers)不证明本机云处理器一定以低完整性／AppContainer 运行。若实际为低完整性，[MIC](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control)可阻止写入 medium 管道和 HKCU 探针；CurrentUserOnly DACL 不等于修改完整性标签。本轮没有修改正式管道权限或关闭隔离。

## 实际隔离结果

最终单根包 `HybridMount.ShellProbe.7837abf0` 使用独立 CLSID、管道和项目内 GUID 根，经 IApplicationActivationManager 正常激活。实际原生策略读回分别为 0／1／2，根身份保持；真实 IShellItemImageFactory 以 THUMBNAILONLY 请求图像。

| 策略 | 图像 HRESULT | 识别像素／IPC | 正文水合 |
| --- | --- | --- | --- |
| Partial | 0x8004B200 | 无／0 | 未发生 |
| Progressive | 0x8004B200 | 无／0 | 未发生 |
| Full | 0x8004B200 | 无／0 | 未发生 |

预先生成应用缓存后的 warm 请求仍失败，故未进入 cold 完成通知缓存刷新验收。HKCU 原生探针为空，不能据此断言处理器从未激活；真实宿主完整性、包虚拟化、初始化、归属和权限仍需进一步定位。普通包保持原有不绑定自定义处理器的门控，实验包不能宣称端到端有效。

早期逐根矩阵在 Register 返回 0x80070490，且清理报告没有覆盖半注册残留。改为单根复用后矩阵完整执行，但没有证明此前失败的根因。将根从 AppData Temp 移到项目产物下也不能单独解释全部失败。[MSIX 虚拟化机制](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)支持路径视图差异的假设，但没有官方“禁止 Temp 根”限制。无包身份枚举正常，不能称全局 WinRT 注册被破坏。

早期 7 项精确测试记录、后续 36acc021 的 2 项及原生空根均已按证据备份、核对 ID／SID／AUMID／路径／唯一命名空间后精确收尾。最终单根通过 0.0.9.31 恢复服务清空注销，Deleted=5、Completed=true，检查原生及HKLM引用后卸载临时包；用户重装后仍发现HKCU每根入口，故此前不能据包内检查宣称包外清理完整。测试始终使用本地 HTTP，不访问正式服务或已取消 NAS。正式三个根的注册和目录身份只读比对保持，未重启 Explorer、修改 ACL 或重建根。

## 后续定位与人工待办

1. 在正式升级后的同一运行模式记录完整诊断，核对根 AUMID、当前包、原生策略、AllowPinning／WinRT 可见性、文件级 Shell 与 CFAPI；自定义右键菜单存在不能替代原生菜单验收。
2. 在有独立包身份的受限试验中记录实际 COM 宿主 TokenIntegrityLevel／TokenIsAppContainer、激活与初始化 HRESULT、归属检查和管道失败码；不得为收集证据扩大正式 IPC 权限。注册表探针为空只作为缺失证据。
3. 验证已缓存小 PNG／BMP 与严格范围读取可用的冷文件；确认来自内容的图像、无额外 PUT、无占位正文水合，再检验 cold 请求完成后普通 Explorer 刷新。缓存提取诊断不能替代普通浏览通过。
4. 原生固定／释放菜单、状态列及缩略图分别验收。Progressive 迁移仍是未完成项，需独立证明读取、固定、释放、取消及旧根原位更新安全，不能用普通文件覆盖或重建根替代。

用户已说明暂时无法回应，所有需手动 Explorer／GUI 操作和安装升级的项目均列入待办，不以等待用户作为停止自动验证的理由。

## 追加：包内完成与包外入口残留

用户重装0.0.9.32后确认四项旧入口仍在，清理代理已在无包身份环境备份并精确移除8键4值，目录及内容保持，目标注册剩余0。详见本版验收追加章节。

关键对照是7837abf0隔离根的包内日志包含NameSpace和CLSID两项备份，并报告删除完成，而包外相同GUID仍存在。微软MSIX虚拟化文档说明HKCU写入可进入每包私有hive，读取合并视图、卸载清理私有hive；这能解释当前现象，但不能据此确认具体tombstone实现，仍保留随后重建的可能。打包模式的完成验收必须包括包外视图。

选择性RegistryWriteVirtualization支持静态HKCU排除路径，但没有运行时随机GUID或值级匹配承诺。不能为了动态每根CLSID／HideDesktopIcons值而全局关闭虚拟化、排除整个CLSID或共享隐藏键。后续应使用明确受限、确认不虚拟化的维护执行面，逐项消费带ID／路径／所有者的备份proof，再从包外复核；其余产品配置继续原有语义。本版未修改生产清单的虚拟化策略。

官方排除机制参考：[Flexible virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)、[RegistryWriteVirtualization](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-virtualization-registrywritevirtualization)、[ExcludedKey](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-virtualization-excludedkey)、[unvirtualizedResources](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization#the-unvirtualizedresources-restricted-capability)。这些资料是后续设计依据，不算生产清单变更或验收通过。

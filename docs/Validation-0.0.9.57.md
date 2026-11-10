# 0.0.9.57 群晖 File Station 修订整合验收

2026-11-10，基于codex/native-backend的9656d81实施；工作区开始时干净。来源#38固定于3e496b0，按[修订计划](Plan-0.0.9.57-Synology.md)提取功能，未直接采用弱覆盖路径。完整日志、TRX、包外证明与摘要位于artifacts/validation/00957-synology/。本版独立本地提交，不推送、不改动master；GitHub #38仍Open、来源保留。

## 实际实现与兼容边界

新增群晖源、配置窗口、容量及完整轮询接入。API版本限制与参数编码、SynoToken、OTP和DID、失败锁存、五分钟一次性会话交接实现；认证票据由应用生命周期和表单共同拥有，关闭及退出等待资源归还。

文件元数据包含本轮MD5，普通读取只发布完整验证快照；同秒同长度修改可改变内容版本，缓存及分块不拼接不同版本。摘要工作合并并受队列调度，MD5启动后先在独立短期限内领取回执，再按任务ID取消。严格远端缩略图立即不支持，主程序及Shell不为此完整下载；完整本地文件仍由C++解码。

条件覆盖、删除和移动发请求前拒绝；新建按连接和父目录验证同名报错语义，省略overwrite、禁隐式父目录、mtime毫秒，回执及摘要核对后完成。未知结果沿用原恢复事务，不创建第二套日志。原通用冲突保护保持，.hm-*.tmp外部文件可见。

FileSourceKind.Synology=5和可空证书指纹追加；v6供群晖，纯WebDAV／S3仍导出v5，读取v1—v6，.hymcfg、可选密码和算法保留。群晖设备令牌不导出或导入。IFileSource、IpcMessages、MountDatabase及MountManager.Conflicts与.56无差异；源项Compile267／Page13／ApplicationDefinition1。包资源检查增加群晖窗口，共14项实际PRI资源，两个最终签名包均核验通过。

## 自动验证结果

| 实际配置 | 结果与证据 |
| --- | --- |
| x64 Debug／Release非打包 | 最小及最终主工程／两个C++工程编译通过；final-x64-*-Unpackaged.log |
| x64 Debug针对性 | 158/158，0跳过；final-targeted-Debug2.trx／log |
| x64 Release广泛 | 1539/1539，0跳过，群晖44项；final-regression-Release.trx／log；明确排除FailedMountTests、OrphanedMountRecoveryTests、ClearRemovalTests这三个.33停止专项类 |
| ARM64 Release | 产品签名包及测试工程交叉编译通过；未在ARM64主机执行 |
| 双架构包 | 签名、版本、身份、架构、自包含、原生无CLR头及14项PRI核验通过；final-matrix-driver.log与final-package-14resources-*.log |

群晖44项覆盖API协商、两代认证响应夹具、失败锁存与并发、正常过期、显式OTP替换旧设备令牌、OTP无DID会话交接、票据失效／过期、证书、JSON参数、完整缓存、同秒同长度变化、版本过期、取消MD5任务、新建竞争及跳过回执、回执丢失、禁破坏性修改、正文租约、跨根并行、共享优先级和独立取消、备份与设备令牌清除。

真实CFAPI联合响应器在新建NTFS根验证：固定无需打开即可下载，释放后脱水且占位身份保持；本地修改保留冲突、远端原内容保持，无关新文件继续上传；冲突比较不开放使用本地覆盖。每轮根注册与移除证明为cfapi-*-registered／removed.json，最终Owned=false。备份导入和导出路径验证不建立源连接。这些是回环／内存协议响应联合Windows验证，不等于真实NAS验收。

Release广泛回归还实际执行原生模块不加载CLR、缓存命中／冷未命中无源读取、本地解码、原生运行时及托管逻辑回归。Debug本轮只运行针对性集合，不称重新完成Debug全量。

## 独立MSIX与包外Shell

临时包为dwzcn.HybridMount.NativePreview.Probe36a14d22_0.0.9.57_x64__e43t74r3a3xm8，独立CLSID及新GUID根，未安装正式NativePreview产物。使用本版测试程序集、HybridMount.exe验收入口及生产HybridMount.Stock.exe。

真实Shell自动取得缓存图和完整本地图片，包外Outside.Passed、主结果Passed和Cleanup均为true；原生固定／释放菜单与状态属性保留。源请求8→8，检查文件内容、身份、Pin、InSync等事实保持。实际AUMID为本轮临时包Family!App，不能冒充正式包安装验收。证据desktop3Synology57-36a14d22/result.json、outside.json及NativeTrace。

同包另建空根验证卸载：包外uninstall-proof.json的NativeRootAbsent／RegistrationAbsent／NamespaceAbsent全为true，临时包已不存在。随后独立最终审计两根的同步根、CLSID和NameSpace注册均缺席，验证空目录后删除。没有重启Explorer，没有对正式相册或已有挂载执行操作；本次未开展人工WinUI／Explorer目视或滚动／鼠标专项。

## 签名产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.57_x64_Test/HybridMount_0.0.9.57_x64.msix | 835804837e5f6f66e7c93b6ce8bd01ab1eb9a009f432d88208e73f898cf556cc |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.57_arm64_Test/HybridMount_0.0.9.57_arm64.msix | 04d10eb0f1ba0dd2089267ad81eadcc5fc8a72e28a125ba3b0c2ed4b0b40f21c |

签名证书为本机既有CN=dwzcn测试证书；不表示正式安装或商店发布。摘要及源码／公共兼容证明见final-artifacts.json。

## 阶段失败及精确收尾

保留初次编译、测试夹具适配和中间失败日志：优先级枚举、StackPanel启用属性、测试断言与定义存储契约修正后编译通过。最初协议／边界31/33的两项失败来自隔离原生宿主仍为.56，重建.57后33/33。测试曾错误重复登记内建适配器，改用工厂已有扩展入口验证交接。

取消测试真实暴露MD5任务已创建但ID回执尚未读取的窗口，修订为独立五秒回执再停止；相关复测及最终集合通过。首次最终Debug157/158中，共享优先级测试在Task.Run实际入队前断言，补齐按真实队列条件等待后最终158/158；没有放松优先级或取消行为断言。早期结果不替代最终证据。

普通测试注册458项、临时包遗留2项，均在当前SID、精确路径、基线和无活跃根引用核验后备份并删除，目标剩余0。30个本轮测试缩略图库经只读归属核验、SHA256备份后清理；基线条目保留。证据artifacts/validation/00957-test-*、00957-packaged-*及本目录cleanup-*.log、final-artifacts.json。清理驱动曾把未设置的LASTEXITCODE误判为失败，注册本身已清理成功；后续使用实际命令状态完成余项，没有重复覆盖首次删除证据。用户已清理的三个旧Thumbnail-00945入口未处理。

## 未执行与保留限制

DSM6.2与DSM7.x真实设备、MFA／设备令牌各实际策略、上传竞争及NAS断线实测、人工配置窗口／Explorer、正式安装升级与ARM64实机均待测。API响应夹具和交叉编译不能替代这些验收。完整MD5核对与整文件快照可能增加NAS负担和首次读取等待。

首版不支持可靠覆盖、删除／移动或仅联机远端预览；不以弱版本或整文件预览下载绕过限制。.56记录的Debug大媒体十秒超时和旧根图标查询问题本轮未修复、未执行相应Debug／旧根复验，保持待测；Release相关原有媒体回归本轮实际通过。未执行.33停止专项，不推送、不修改master。

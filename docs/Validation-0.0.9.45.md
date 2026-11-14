# 0.0.9.45 原生缩略图验收

## 实现与边界

基于codex/native-backend的305b62e。四工程、Debug／Release、x64／ARM64；当时的预览身份包身份与正式产品和另一部署模式隔离。仅使用随机NTFS根、回环WebDAV和独立临时MSIX，不操作正式挂载、真实服务或其他账户，不重启Explorer，不补跑.33停止的三个专项类。

CloudFiles合法完整子组中未实现处理器明确Clsid=GUID_NULL，不为这些空值登记COM类。仅真实菜单和缩略图CLSID由原生ExeServer提供，独立菜单Verb不变。省略Clsid的旧方案会使系统丢失状态／缩略图关联；desktop3完整子组改为GUID_NULL后两次隔离包复现通过。现代命名空间／仅缩略图子组被26100 SDK及本机包模式验证拒绝；不绕过校验、不升级SDK或全局接管文件类型。原始失败及模式证据保留于00945-binding-*、00945-runtime-manifest-reader.json。

完整本地文件先缓存，未命中由受保护纯C++工作进程解码，2秒、512MiB Job、128MiB／512次预算。普通文件不转换占位，云文件使用protected handle，前后核对正文身份、长度、mtime、完整范围及真实云状态；保护期间允许只读访问元数据USN更新，关闭返回精确最终Stamp，发布后不伪造Pin／InSync。

冷文件仅有效缓存，未命中不联网、不水合、不启动主程序。应用已运行且设置开启时独立STA观察进程读IShellWindows／IFolderView2，每秒优先当前目录可见媒体；当前连接代次及根身份复核，提示独立有界、可丢弃，不阻塞关键回调。目录变化取消旧工作并支持快速返回重排，发布与取消串行；后台严格远端8MiB／32次、256KiB窗口及10秒期限。C#是SQLite缓存唯一写入者，Shell不写库、不通过NamedPipe取位图。新像素上限6400万；系统编解码器缺失或预算用尽及时回退。

## 已执行自动验证

| 验证 | 实际结果 | artifacts/validation证据 |
| --- | --- | --- |
| 最终相关回归 | 42/42，0跳过 | 00945-final-targeted-fixed.log／.trx |
| 首次新增大图夹具 | 100MiB BMP触发10秒期限，28/29；保留失败。之后拆开字节预算和像素门限，用3355万像素压缩PNG独立验证 | 00945-final-targeted.log／.trx |
| 真实MSIX自动选择 | 冷／完整文件均取到缓存识别图案，缓存与正文像素不同；状态1／3，系统固定／释放菜单存在 | 00945-work/desktop3Null-55130d02/result.json；desktop3VisibleNull-219df5a3/result.json |
| 首次未命中后刷新 | 首次GetImage无图；后台只发严格Range GET；Notify后普通系统GetImage得到64×32真实图片，冷文件保持未水合 | 00945-work/desktop3NullFinal-e7ecd135/result.json及outside.json |
| 卸载系统注销 | 实际包入口注册独立零字节根，退出保留注册；卸载后包外确认原生根／SyncRootManager／Namespace全部不存在，测试根目录清理 | 同目录uninstall-proof.json |

| x64 Debug／Release广泛回归 | 各1351/1351，0跳过；在最后目录观察补充前执行 | 00945-debug-regression.log；00945-release-regression.log；contracts-x64-*.trx |
| 最终观察补充后相关回归 | 42/42，0跳过 | 00945-observer-clean-final-targeted.log／.trx |
| 真实Explorer浏览触发 | 脚本打开独立测试目录，纯C++观察触发late文件生成；没有注入合成浏览提示。严格Range、冷文件未水合、自动提取、状态／菜单及卸载核验通过 | 00945-work/desktop3Explorer-09eb02c4/result.json、outside.json、uninstall-proof.json |
| 最终x64 Debug非打包编译 | 通过 | 00945-final-unpackaged.log |
| x64／ARM64 Release自包含签名MSIX | 版本、架构、五节点合法组、仅两个真实COM类、独立菜单、维护入口、纯原生CLR头及资源检查通过 | 00945-final-x64-package.log；00945-final-arm64-package.log |
| ARM64测试交叉编译 | 0警告／0错误；未执行实机测试 | 00945-final-arm64-tests-compile.log |
| 测试资源收尾 | 精确快照核验后清理注册和32个独立测试缓存；23个已知根ID、20个Namespace及26个已知测试包包外核验零目标残留 | 00945-*-test-registry-*.json；00945-test-cache-cleanup.json；00945-independent-registration-audit.json |

脚本化Explorer补测发现两个真实缺口：云目录PIDL不提供文件系统路径，补IPersistFolder3目标及仅本地file:///地址回退后继续核验根身份；GetVisibleItem返回搜索起点之后的项，现从-1起、以下一个已返回索引继续，避免隔项漏掉可见媒体。窗口过滤、目录路径与可见项目失败的中间证据保留在00945-binding-explorer*.log及各独立目录，没有用最终通过覆盖原失败。

部分失败探针在异常出口未卸载临时包，且旧夹具保留了额外生产CLSID，干扰一次COM复测（41/42，单项复测0/1）。已依据本轮package.json和Cleanup结果精确关闭5个纯Shell测试宿主、卸载5个测试包并清理已确认空目录；夹具生成器改为只登记本轮CLSID。清理后42/42通过，原00945-observer-final-targeted及00945-native-com-recheck证据保留。未关闭或卸载用户正式应用。

缓存清理工具最初未关闭Python只读连接导致单库Win32共享冲突；显式关闭后依据同一备份校验重试成功，原始输出保留。不将这种夹具收尾失败记为产品数据库损坏。

## 交付产物

- x64：artifacts/packages/x64/HybridMount_0.0.9.45_x64_Test/HybridMount_0.0.9.45_x64.msix。
- ARM64：artifacts/packages/ARM64/HybridMount_0.0.9.45_arm64_Test/HybridMount_0.0.9.45_arm64.msix。
- SHA256见00945-final-package-hashes.json。

最初卸载夹具错误要求零字节占位仍存在，但Windows已同时清走文件；这是清理断言错误，现允许0或1个已知零字节项。原始uninstall.err保留在desktop3NullFinal-287c246f，不覆盖原证据。

## 人工待测试

- 真正Explorer侧栏及磁盘路径的目视图标／状态列／系统菜单、多窗口切换及设置界面。
- 正式安装升级、原用户媒体样本与本机HEIC／HEIF等编解码器差异。
- ARM64实机运行及Explorer激活；交叉构建不能记为实机通过。

IShellItemImageFactory自动选中本产品处理器且返回识别像素，证明系统绑定链路；显式COM结果单独保留，不替代该结果，也不等同于Explorer目视验收。


- x64 SHA256：`CC0FEFB48CE40299181C001474D1B81108184F50A2B40358B4EAE24D631965A6`。
- ARM64 SHA256：`70F99C87767E1DDE420B25284752614B58465B80BA98C5E3B4BAC87B99A57200`。

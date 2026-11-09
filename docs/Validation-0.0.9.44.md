# 0.0.9.44 原生 Windows 后台实验分支验收

## 范围与隔离

基于0.0.9.42的6d4b1a2建立codex/native-backend；不包含.43，不合并或改写master。首次实验版本0.0.9.44，包身份dwzcn.HybridMount.NativePreview，四工程，仅Debug／Release、x64／ARM64。原定代码及自动验证预算4—7个工作日，桌面和实机等待另计。

- 当前账户注册表：HKCU\Software\HybridMount.NET\NativePreview\<Packaged或Unpackaged>；根、公共IPC v2实例和私有管道使用HybridMount.NativePreview.<模式>前缀。
- 打包数据使用实验包LocalState，非打包使用%LOCALAPPDATA%\HybridMount.NET\NativePreview\Unpackaged；缩略图固定在实验物理目录<模式>\Mounting\Thumbnails\<挂载GUID-N>.db。
- 菜单CLSID为59964FC1-D076-4DC1-9778-01F5833A5D4A，缩略图CLSID为A749B295-9CB5-4F08-98D4-00EB47345E09。自动缩略图绑定保持门控，交付包采用Menus和空CloudFiles声明。
- 仅使用独立随机NTFS测试根、回环协议服务和临时测试包；未操作正式挂载、真实远端或已取消NAS，未重启Explorer。按用户要求不补跑.33停止的移除专项。

## 同版本维护：开始同步窗口死锁

首次交付91402f2后，用户反馈新增挂载点击“开始同步”卡住。只读采集当前VS打包Debug进程的两次线程栈，都停在窗口线程ConnectCoreAsync→InspectRegistration→IsOwnedRoot→NativeRuntimeClient.Invoke的Task同步等待；进程没有退出。证据：00944-connect-live-managed-stacks.log、00944-connect-live-managed-stacks-repeat.log。

原生运行时外层等待虽然使用ConfigureAwait(false)，公共IpcProtocol内部帧头／正文读写和Flush仍捕获调用方SynchronizationContext；同步Invoke占住窗口线程后，帧I/O续体无法被调度回来，形成互等。新建不泵消息的窗口上下文，延迟分片读、分片写和真实私有管道查询在旧代码0/3，形成与现场调用边界一致的独立复现。回归只操作内存帧、独立原生进程及随机测试路径，不连接用户挂载。

修复将帧全部I/O续体脱离调用方上下文；连接先登记所有者、取消和连接中状态，再通过ForceYielding离开窗口线程。该切换覆盖手动连接、新建连接及凭据更新后的内部连接；后台已预热也不在窗口线程执行同步本机查询。保持帧完整性、写入结果核对与现有取消／退出所有权。

| 维护验证 | 实际结果 | artifacts/validation证据 |
| --- | --- | --- |
| 修复前分片读、分片写、真实原生查询 | 0/3，全部依赖窗口消息泵 | 00944-connect-ui-before.log／.trx |
| x64 Debug最小非打包编译 | 通过 | 00944-connect-fix-debug-build.log |
| 第一轮相关回归 | 49/50；新增连接测试的断言框架自身捕获上下文，修正测试辅助器 | 00944-connect-fix-debug-targeted.log／.trx |
| 最后Debug相关回归 | 50/50，0跳过 | 00944-connect-fix-debug-final.log／.trx |
| 最后Release相关回归 | 50/50，0跳过 | 00944-connect-fix-release-final.log／.trx |
| x64／ARM64签名包、实际包内容检查 | 通过；未部署 | 00944-connect-fix-x64-msix.log；00944-connect-fix-arm64-msix.log |
| ARM64测试交叉编译 | 0警告／0错误，未实机运行 | 00944-connect-fix-arm64-test-compile.log |
| 双架构托管／原生二进制边界 | 无CldApi导入／无CLR头 | 00944-connect-fix-binary-audit.log |

相关回归覆盖上述四项新增案例、公共IPC、连接探测、断开／退出／取消、原生供数和后台失败。没有重新运行首次交付的1336项广泛集合，也没有补跑.33停止专项。30个新增32位测试CLSID先按旧基线、测试目标、所有者及引用捕获精确快照，删除前再次比较；测试根、缓存及注册目标最终无残留。证据：00944-connect-registry-backup.json、00944-connect-registry-cleanup.json、00944-connect-cleanup-audit.json。

保留现场主程序和原生后台，没有强制结束、删除挂载或操作远端。当前VS进程仍加载修复前DLL，停止调试、重新生成并启动后的原始按钮场景列待测。下文1336/1336等广泛记录属于首次交付91402f2，不能当作本维护补丁的新测试结果。

## 代码完成

C++拥有CFAPI结构、回调参数副本、缓冲区、保护句柄、操作完成和同步根连接，负责注册、占位、水合／脱水、固定／排除、冲突替换、本地收尾及状态属性；C#保留WinUI、ViewModel、托盘、WebDAV／S3、凭据、同步事务、索引和数据库唯一写入。生产托管程序集不导入CldApi.dll；旧ABI仅在测试条件下保留。

私有认证双NamedPipe核验父子进程、SID／会话、部署身份及启动随机标识；UTF-8控制消息有界，64位值用十进制字符串无损编码。正文256KiB分块并背压；控制响应、回调和检查点独立调度，事件携带挂载及连接代次。正文队列满时背压，命令队列满只拒绝当前请求，不关闭其他挂载共享的通道。

本地读租约由原生进程持有保护句柄，托管侧仅持GUID和有界正文。写入前持久保存检查点，回执丢失先核验，不重放本地变更。后台故障停止生产者、保留注册与恢复记录；按需重建空后台只供核验，显式连接建立新代次。退出先停止所有挂载，再关闭共享后台，过期水合事件被拒绝。远端快照水合保留原两分钟期限。

WinRT根查询先完整物化快照，仅对只读NO_MORE_ITEMS有界重取。注册结果不明时核对根身份、路径、版本、AllowPinning和属性定义，不重复注册。原生实际根／属性写入及C#精确Shell清理共用当前SID互斥；无变化查询不进入锁。部分属性写入失败仍返回精确前后元数据证明，不误建上传代次。保留.38排除改名、.40冲突授权、.41冷文件延迟EXCLUDED和.42安全日志。

CFAPI后台、COM宿主和媒体解码使用独立原生进程角色。缩略图直接只读已有SQLite BLOB库，不创建／升级数据库或写访问时间；校验schema、账户、根／文件身份及内容代次，忙、损坏、失效或缺失及时返回。GetThumbnail全部路径不发管道、不联网、不水合、不启动主程序。动态菜单仅根据本地元数据筛选，执行前再次核验：完整文件显示释放空间，未完整文件显示始终保留，排除文件隐藏两项。

## 首次交付91402f2的自动验证

日志和TRX保存在artifacts/validation，以下文件名均相对此目录。最终测试过滤条件为：

```text
FullyQualifiedName!~FailedMountTests&FullyQualifiedName!~OrphanedMountRecoveryTests&FullyQualifiedName!~ClearRemovalTests
```

上述三个.33专项类未运行；最终测试集内0跳过，不能据此声称已补验.33。

| 验证 | 实际结果 | 证据 |
| --- | --- | --- |
| 首批x64非打包最小编译 | 通过 | 00944-batch1-build.log |
| 原生缓存直读／无CLR宿主 | 3/3通过 | 00944-native-cache-final-tests.log／.trx |
| 原生图片、视频、EXE解码及取消／预算 | 11/11通过 | 00944-native-hosts-tests.log／.trx |
| 私有管道、64位精度、并发及退出 | 阶段2/2；最终含1024并发突发查询 | 00944-runtime-guid-tests.log；最终广泛TRX |
| 新NTFS根注册、供数、释放及注销 | 1/1通过 | 00944-native-flow-tests.log／.trx |
| 连接代次、后台故障重连、属性及占位生命周期 | 阶段13/13通过 | 00944-generations-targeted.log／.trx |
| 主挂载断开后的原生COM缓存位图及冷文件保护 | 4/4通过；无CLR、无新增远端读取、无水合 | 00944-native-com-final.log／.trx |
| 最终x64 Debug广泛回归 | 1336/1336，0跳过 | 00944-debug-winrt-serialized.log／.trx |
| 最终x64 Release广泛回归 | 1336/1336，0跳过 | 00944-release-winrt-serialized.log／.trx |
| 最终Debug／Release非打包构建 | 通过 | 00944-final-debug-unpackaged.log；00944-final-release-unpackaged.log |
| x64／ARM64签名MSIX及实际包内容检查 | 通过；架构、自包含、版本及菜单／维护入口正确 | 00944-final-x64-msix.log；00944-final-arm64-msix.log |
| ARM64测试交叉编译 | 通过，0警告／0错误；未运行ARM64测试 | 00944-final-arm64-test-compile.log |
| 双架构生产二进制边界 | 托管程序集0.0.9.44且无CldApi导入；Native.exe／Ext.dll无CLR头 | 00944-binary-boundary-audit.log |

### 真实临时包与包外核验

使用独立随机dwzcn.HybridMount.NativePreview.Probe身份、随机根GUID及隐藏测试relay；正常原生映像从同目录启动，不启动正式主应用。实际包身份、根AUMID=<测试包family>!App和AllowPinning=true已验证，原生进程模块列表无coreclr／clr／hostfxr。独立非打包进程读取Explorer所见注册。临时根和包清理后再次包外核验无残留。

证据：00944-msix-native-external.json、00944-msix-native-proof.json、00944-msix-native-acceptance.json、00944-msix-native-cleanup-audit.json、00944-msix-probe-final.log。非打包正常映像探测见00944-unpackaged-native-proof.json。

这只证明原生身份、注册和接口边界；不代表Explorer已自动选择缩略图处理器或正式安装升级通过。

### 中间失败与修复复测

- 早期原生标记19/24、相关回归64/65失败保留：测试跨进程Pin操作、短暂占用及检查点异常分类问题，后续修复并纳入最终回归。
- 首轮广泛1306/1334；随后1328/1334、1333/1334、1334/1336和1335/1336等阶段证据仍保留。对WinRT枚举并发、根清理与属性写入竞争、旧测试边界和本地预览精确内容证明逐批修复。
- Release曾在共享通道EOF后947/1335通过。随后补充有界队列背压、空后台只读恢复及安全传输阶段证据，并以最终Debug／Release1336/1336复测。早期现场缺少足够阶段证据，不能宣称已证明EOF必由队列满触发。
- 不用最终通过覆盖上述失败记录；中间构建日志、TRX及补丁批次日志均保留在artifacts/validation。

## 本轮测试现场收尾

独立非打包进程枚举真实HKCU／HKLM的32／64位视图。核对实验前缀、当前SID、测试路径、所有者、唯一引用和精确树快照后，先通过原生运行时注销6个仍存在的测试根，再精确收尾注册。本轮纳入26个残留根注册、3250个测试CLSID及对应入口／隐藏值共3308项；完成后全部目标不存在，旧版基线候选不按前缀批量删除。

仍存在的36个随机测试目录先经原生查询确认无根，再限定到其绝对测试路径删除。190个实验缩略图库先按库内挂载GUID／SID／测试路径或完全空库核验，保存原字节备份后精确删除。独立核验没有NativePreview根、临时包或原生进程残留。旧版.43历史快照有3项当前缺失，不在本轮备份或删除集合内；未按过时快照重建。

证据：00944-test-root-audit-final-before.json、00944-test-cleanup-ownership.json、00944-test-native-before.json、00944-test-native-cleanup.json、00944-test-registry-backup.json、00944-test-registry-extra-backup.json、00944-test-registry-cleanup.json、00944-test-registry-extra-cleanup.json、00944-test-path-native-audit.json、00944-test-directory-cleanup.json、00944-test-cache-cleanup-backup.json、00944-test-cache-cleanup.json、00944-cleanup-final-audit.json。辅助器和原字节备份仅存忽略的artifacts/native-work，不纳入产品。

## 交付产物

以下产物已重新生成，包含开始同步修复；仍为0.0.9.44、自包含、Menus配置及NativePreview身份。首次交付原哈希见91402f2中的本文件，本表为维护修复后的当前哈希。签名使用已有当前用户证书3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0；未导出私钥。

| 架构 | 相对仓库路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.44_x64_Test/HybridMount_0.0.9.44_x64.msix | 7F2F497B27E172586E281EAE3038CDE5A5CCCA314687FCC044DAECEF00A478E0 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.44_arm64_Test/HybridMount_0.0.9.44_arm64.msix | CF6FCFE84C316E7AF1620745C997813FB4476781873209E3DBB4982F4B4B6AC0 |

nlohmann/json 3.12.0保持完整MIT许可随源码和包交付；原生头文件SHA256为5F09D1EEBE9B3557F21DF155869AF13BCDE79B74A0748F82CA946E8CC088AAA0。

## 未完成与待测

- **未完成／门控**：Explorer自动选择云缩略图处理器，同时保留正常文件图标、原生状态列和系统菜单；显式COM返回位图不能代替此项。验证前不启用自动注册或用户开关。
- **待测**：当前VS旧进程停止调试并重新生成后的“开始同步”原场景，真实WinUI操作、托盘与退出，侧栏及磁盘路径的Explorer列／图标和动态菜单目视，正式实验包安装／卸载／升级，主程序退出后Explorer自动缩略图选择，ARM64实机运行。
- 不接管现有正式挂载，不合并master、不推送；只在codex/native-backend完成独立本地提交。GUI和实机待测不记为测试通过。

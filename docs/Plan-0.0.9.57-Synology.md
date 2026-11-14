# 0.0.9.57 群晖 File Station 审查与修订整合计划

基线为codex/native-backend的0.0.9.56（9656d8179dc585ff2ded2409ffa51684400d5b57），来源为[PR #38](https://github.com/dwzcna/HybridMount.NET/pull/38)固定提交3e496b07767eb87263c72185076e227995eda0c6。按用户确认的安全读写首版及DSM6.2／7.x兼容目标实施。保留HybridMount.exe、四工程、当时的预览身份包／根／管道／数据身份；不推送、不合并master，不关闭#38或删除来源。

## 审查结论

原PR未编译或运行测试。静态发现PreviewRangeResult.Count不存在；实施编译还发现不存在的MountingPriority.Metadata和StackPanel.IsEnabled。弱版本仅为大小与秒级时间，无法区分同秒同长度修改，也不能安全拼接范围或复用缓存。原替换先删除目标再改名，没有持久恢复阶段，通用冲突层还为具体协议绕过条件保护；原方案不予采用。

认证仅限制单次调用重登，缺少连接级失败锁存；设备令牌不可用时，OTP只读测试到实际创建连接之间没有会话交接。原实现直接采用服务端maxVersion，遗漏SynoToken；Upload的overwrite=false是同名跳过，且mtime应按毫秒发送。模块边界及无条件v6导出还存在既有测试兼容缺口，.hm-*.tmp通配隐藏会遮蔽用户文件。

依据：[File Station官方API](https://global.download.synology.com/download/Document/Software/DeveloperGuide/Package/FileStation/All/enu/Synology_File_Station_API_Guide.pdf)、[DSM登录API](https://global.download.synology.com/download/Document/Software/DeveloperGuide/Os/DSM/All/enu/DSM_Login_Web_API_Guide_enu.pdf)。文档是实现参考，不代替两代DSM实机验证。

## 修订方案与接口

- FileSourceKind追加Synology=5，配置增加可空的PinnedCertificateSha256，空值不写入JSON；不修改IFileSource、公共IPC v2或产品SQLite表结构。
- Auth实现v3—v6并优先v6；List／Download／MD5／CreateFolder使用v2，Upload支持v2／v3，与服务器声明取交集。按发现路径、requestFormat及实际可用能力调用；无关接口不阻断连接。入口明确不支持时才在query.cgi与entry.cgi间降级。
- OTP、SID、SynoToken仅内存；DID使用DPAPI。五分钟票据按服务器、信任策略及账号凭据一次性交接，不能跨身份复用；关闭、到期和应用退出归还。显式OTP优先于旧设备令牌，永久认证失败锁存，正常过期最多一次共享恢复。
- 普通读取使用摘要版本及完整验证快照。完整轮询逐文件补充远端摘要，缓存键包含内容版本；下载前后及流摘要一致才发布。共享工作合并、独立取消，持有实际所有者直至结束。MD5启动一旦发送，以五秒独立回执期限领取ID，再取消／停止，避免取消时遗失已创建任务。
- 严格远端预览直接返回不支持，不登录、不做MD5、不下载或水合；完整本地内容继续使用C++链路。首次打开完整快照及远端MD5可能增加等待，界面与说明明确边界。
- 覆盖、删除及移动在发送前拒绝，不增加弱替换恢复系统。新建用明确同名报错语义、禁隐式父目录；首次写入按连接和父目录验证同名拒绝及原内容保持，回执后核对摘要。探测只过滤／清理精确拥有的对象，结果不明保留证据；用户内容未知结果沿用既有持久事务只读核对。
- UI经MountsViewModel和应用所有权发起操作；任务队列正文租约、优先级继承及代次保护复用现有结构。备份含群晖时使用v6，纯旧协议继续v5，读取1—6；导出和导入均清除群晖设备令牌。

## 实施、验收与交付

全部文本经patch/0.0.9.57_*.patch、git apply --check、git apply，成功后删除补丁。按协议、界面／生命周期和最终验证分批实施；原估代码与自动验证3—5工作日，真实设备等待另计。实际结果见[本版验收](Validation-0.0.9.57.md)。

验收覆盖版本协商、认证失败合并、OTP交接、证书、同秒同长度变化、完整快照、多块缓存、取消及无迟到发布、新建竞争、未知回执、冲突不阻塞、正文执行位、共享优先级、备份1—6和旧协议回归。先最小x64编译及针对性测试，再必要Debug／Release、非打包、双架构签名包及ARM64交叉编译。

只在新建测试根和独立临时MSIX验证CFAPI、Shell自动出图、菜单／状态和卸载注销，并从包外核对注册／缓存清理。真实DSM6.2和7.x、人工WinUI／Explorer、正式安装及ARM64实机分别列待测；不操作正式挂载，不重启Explorer，不恢复.33已停止专项。.56已知Debug大媒体和旧根图标项目不扩大到本批。

每版独立一个本地提交：0.0.9.57｜修订整合群晖 File Station 支持。

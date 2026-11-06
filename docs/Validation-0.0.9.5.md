# HybridMount 0.0.9.5 验收记录

2026-11-06，Windows x64。范围为本地删除后同名替换的上传基线和已移除挂载遗留远端任务；开发及自动验证预算60–100分钟，桌面、真实服务和实机等待另计。本版在819c287之后独立实施，仅本地提交、不推送。

## 原因与代码完成

- 只读正式SQLite（mode=ro、PRAGMA query_only=ON）确认：D:\myweb 的管道特性表.xlsx旧索引已标记本地Deleted／Protected，但远端Presence仍Present，保留旧版本。旧扫描排除Deleted索引，把复制来的普通文件创建为ExpectedVersion=null的新增意图；发送前的历史基线保护因此拒绝。诊断时该任务已由本地删除变成Canceled／代次1，未强制重试或直接修改正式数据库。
- 扫描只允许精确同本地／源路径、已物化、仍未确认远端删除且唯一的历史映射提供原基线。已有索引ID优先，不从歧义历史任选版本。提交复用原映射身份，只有正常条件写和原生提交／事务完成才解除本地删除、保护标记。
- 旧误建新增任务只在未发送、无回执、缺少版本绑定且能核实历史删除映射时补EntryId／ExpectedVersion／原摘要。不清除同代失败、冲突或取消，不自动重发；显式重试走原有条件写。Sending／RemoteCommitted／OutcomeUnknown保持证据并先核对；回执后再次复制的新代内容继续排队。
- 文件／目录互换统一Conflict，远端删除屏障继续禁止复活，外部远端修改不会成为新基线。WebDAV和S3共用队列规则，现有SDK与文件源接口保持。
- 截图EXX11901.shx的Waiting历史属于已移除的旧挂载e84c03cd-d2ca-40cc-b851-5a1c6f3d2991。对应remote_pending已不存在；当前D:\myweb挂载20cbc7e9-7400-42e8-8b14-1a28430dba19的同名事件已Completed。问题是旧展示历史没有结束，不能据旧文字认定当前SHX仍被上传阻塞。
- 移除清理在同一SQLite事务中删除对应运行日志并结束远端历史为Canceled；启动补齐没有定义、也没有remote_pending恢复证据的遗留历史。保留原因和完成数，不伪造Completed。仍有定义的断开任务、恢复日志、快照和上传未知结果保持原状。
- 统一产品、程序集、原生扩展和MSIX版本0.0.9.5。全部非二进制修改先在patch/生成补丁，经git apply --check后应用、删除补丁；三个工程、SQLite v6、配置／备份及上传JSON格式不变。

## 自动验证

| 项目 | 实际结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| 修复前精确复现 | WebDAV／S3同名替换均ExpectedVersion丢失；已移除任务仍Waiting，3/3失败 | 0095-before-observed.log／trx |
| 最小WinUI | x64 DebugUnpackaged通过；隔离输出避免覆盖原运行程序 | 0095-minimal-winui.log |
| 针对性 | 80/80，零失败／跳过；含20项新增回归 | 0095-targeted-final.log／trx |
| x64 Debug完整 | 593/593，零失败／跳过；原生及WinUI构建通过 | 0095-debug-full.log／trx |
| x64 Release完整 | 593/593，零失败／跳过 | 0095-release-full-package.log、0095-release-full.trx |
| 签名包 | x64／ARM64均通过签名及内容验证，签名0错误／警告 | 0095-release-full-package.log、0095-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，0警告／错误；无实机执行 | 0095-arm64-tests-build.log |
| 编译／格式 | 主工程178、测试221项Compile，无重复或漏登记；SDK、中央依赖、MountDatabase和UploadModels与HEAD一致 | 0095-structure.json |

新增回归使用本地HTTP夹具与真实隔离NTFS／CFAPI根，均通过公开clearAndRemove注销测试根：

- 下载来的原占位本地删除、明确观察删除后复制同名普通文件，继承旧版本、完成条件更新、复用原索引且InSync。
- 两协议外部远端修改仍Conflict；WebDAV允许携带旧If-Match的PUT被412拒绝，不能把“请求已发送”等同内容被覆盖。确认远端删除后零目标写入，本地内容保留。
- 旧版Conflict／Failed／Canceled误建记录补回原基线但不自动发送；显式重试成功。原观察代次保持，同代取消不被扫描解除。
- 删除已被持久观察后重启再复制；远端回执后再次同名替换，新代次继续上传，旧回执先正常提交。
- 文件／目录互换、歧义映射、未物化／改名／远端删除映射拒绝；结果不明任务的版本不被扫描重绑，快照保留。
- 成功移除后对应等待历史Canceled且其他挂载不受影响；启动时仍有定义的断开历史、仍有恢复日志或未知上传保持，未联网或重复发送。

测试编写阶段先修正xUnit2031分析器用法；最初同进程删除尚未经过Reconcile，改用公开完整刷新确定性记录删除后，再得到3/3准确复现。第一轮扩展为16/19，失败来自测试错误要求远端删除仍绑定旧基线，以及把被412拒绝的条件PUT计作成功写入；改为验证删除屏障、条件头和远端字节不变，最终80/80及双配置全量通过。早期日志0095-before.log、0095-targeted.log保留，不改记通过。

## 实际启动与正式数据

- 原进程22560曾运行0.0.9.4，实际模式Unpackaged；本轮检测其已退出，未强杀或发送关闭。旧last-session仍为Ready／Completed=false，单凭该标记不能确认退出原因；没有将其记为正常退出测试。
- 0.0.9.5以原非打包模式正常启动，PID29240，生命周期Ready；静默启动、静默第二实例、正常第二实例恢复窗口和挂载外两个缓存请求拒绝均通过。应用保留运行。见0095-unpackaged-launch.log和0095-formal-after-start.json。启动／IPC检查不等于GUI或Explorer目视验收。
- 新进程通过正常启动恢复，将截图旧历史6478f9c07df043e19fe0c5e19a2c2dfc结束为Canceled、完成数仍0；当前同名远端任务d8d4497cf57b42ec84b1fece3d9517df保持Completed。未直接改正式数据库以制造通过证据。
- D:\myweb、D:\s3test、D:\wdavtest三个定义SHA256及卷／目录身份前后一致；原工作簿任务db5fe503d64c4dbca852faf67203970b保持Canceled／Generation=1，未代重试、未改写用户工作簿。证据0095-reported-state.json、0095-formal-before-start.json／after-start.json。
- 当前用户Appx查询没有已安装HybridMount包。本轮生成并核验MSIX，未执行安装或升级，也未复制打包／非打包配置。启动前检查正式定义不包含已取消的NAS；未访问10.0.0.20:5007。
- 本轮写入回归仅使用本地DAV／S3夹具；没有在真实远端创建回归目录或测试项。用户工作簿的实际重试、真实服务同名替换、GUI任务与Explorer目视、MSIX安装升级及ARM64实机继续待测；历史Shell和审查待办保持。

## 产物

签名证书：3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.5_x64_Test/HybridMount_0.0.9.5_x64.msix | 84C58CFFB4CE4C19A9C55D17EB4D429432E2785EC7604476AA1732765F36036E |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.5_arm64_Test/HybridMount_0.0.9.5_arm64.msix | 8914D8681D0746D5A0FF45B289CF6E8F4FF105888B89B750E232023D776A6D25 |

构建入口build/Build.ps1，启动验证build/Test-Launch.ps1。当前新非打包版本保留运行；仅本地提交，不推送。

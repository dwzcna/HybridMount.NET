# HybridMount 0.0.8.8 验收记录

2026-11-04，Windows x64，.NET 10.0.401／VS 2026／Windows SDK 26100。本版限协议模块收拢与必要契约提取，沿用本批 8–14 小时开发验证预算。三个工程、公开调用、SQLite v6、IPC v2、备份 v3、DPAPI 及配置语义保持。归属与边界见 Mounting-structure-0.0.8.8.md。

## 已完成

- 指定 WebDAV／S3 子目录都有实际实现；驱动拆分 Files／Transport／Models／Uploads，专用预览、同步、配额、事件和 SQL 归模块。共用 BorrowedStream 及移动异常保持公共归属。
- 工厂使用策略、会话使用运行契约，公共同步器使用已核实事件提示；模块拥有作用域、恢复、凭据模式和专用持久化。数据库连接、锁、事务和消息确认顺序保持。
- 源码边界回归覆盖 SDK／报文不得越界、公共全名兼容、工厂上下文／探测失败释放、只读测试不绑定或恢复、作用域兼容。

## 已测试

| 范围 | 实际结果与证据（artifacts/validation） |
| --- | --- |
| 最小 x64 Debug | 0088-minimal-a／b／c／d.log；b 首次局部变量重名后补丁修正通过 |
| 分批协议／运行／持久 | 160/160、89/89、111/111；0088-protocol-a.trx、0088-runtime-b.trx、0088-storage-c.trx |
| 最终边界／备份／连接 | 56/56、0 失败／跳过；0088-boundaries.trx／.log |
| 完整 x64 | Debug／Release 各 416/416、0 失败／跳过；0088-contracts-x64-Debug.trx／Release.trx、0088-debug-final.log／release-final.log |
| 签名与交叉编译 | x64／ARM64 0.0.8.8 签名 MSIX 的版本／PE／清单／资源／许可及自包含检查通过；0088-package-x64-final.log／ARM64-final.log。ARM64 测试仅交叉编译，0088-tests-arm64-final.log |
| 目录／v6 边界 | 176 个 Compile、103 个挂载文件各一次，无遗漏重复；旧／新建表结果 29 个 sqlite_master 对象一致；0088-structure-audit.json、0088-compile-items.json、0088-schema-statements.json |
| 真实坚果云 | 0088-real-dav-d764b4a08c20478c8b84f768f8d53ada/report.json：文件系统自动上传和独立 SHA-256、固定新版／身份、重启零字节、远端删除不复活通过，唯一远端文件及本地同步根精确清理成功 |
| 真实 R2 | 0088-real-s3-67747bbc19ae4c289ceba4e9138dbe44/report.json：自动上传 SHA-256、固定完整对账、重启零字节、远端删除通过，列表／HEAD 精度证据与精确清理成功 |

真实测试仅只读正式定义和 DPAPI，使用隔离仓库、唯一文件、独立 NTFS 根，没有批量上传旧内容、创建远端测试目录或改动正式根。未访问已取消 NAS。真实结果与本机模拟驱动／CFAPI 自动化分开记录，不代替 AWS SQS 或 RFC 6578 服务认证。

## 未完成

本版代码范围已完成。初次边界测试夹具省略能力构造参数导致 CS7036，补丁显式未知能力后复测；保留 0088-boundaries-first-build-failed.log。首次 PowerShell 公开验收入口因宿主未定位 e_sqlite3.dll 在建仓库时失败，尚未创建文件源或注册根；显式加载随测试输出的本机依赖后再以新目录运行，两真实闭环通过。未使用旧私有反射或原始授权头清理脚本，未改变杀毒设置。

产物：artifacts/packages/x64/HybridMount_0.0.8.8_x64_Test/HybridMount_0.0.8.8_x64.msix；ARM64 对应目录。签名证书 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

## 待测试

- 真实 RFC 6578、AWS S3＋专用 SQS、GUI／Explorer／安装升级／ARM64 实机。
- 正式非打包进程启动未执行：当前存在自动连接而未注册的用户挂载，保留其配置和现场，非打包构建已验证。
- 历史 TEST-RUNNER-01、SHELL-01、原 TLS／调试器现场及其他审查待办继续单列，不将后续通过当作根因已修复。

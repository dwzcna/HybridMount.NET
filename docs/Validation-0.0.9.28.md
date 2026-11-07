# 0.0.9.28：缺失文件的问题提示收尾

日期：2026-11-07。开发及自动验证预算30–60分钟，GUI／实机和外部等待另计。本次保留三个工程、SDK、公共文件源接口、IPC v2、SQLite及配置格式；独立本地提交，不推送。

## 运行现场

用户提供Visual Studio运行输出后，对原0.0.9.27进程PID 19760进行70秒CLR异常采样，保持目标进程运行。共捕获174条FileNotFoundException事件：87次File.GetAttributes原始抛出和87次异步重抛，均指向`D:\myweb\管道特性表.xlsx`。调用链为MountPathGuard.ValidateOperation → CloudUploadIssueProperty.SetAsync → MountManager.PublishUploadIssuesLockedAsync。

只读核对上传日志，该项已Canceled且ResolvedUtc已保存，文件已不存在，ShellState仍为ClearPending。清除分支未遵守失败后的30秒退避，每轮750毫秒重新尝试。采样证明这一组重复异常的来源；未采集的启动阶段异常不能一并归因。诊断工具只读正式库，未手改任务或配置，未启动、暂停、结束或重启正式程序。

原始采样见`artifacts/validation/exception-trace-0120efc10b1b41ea881cc6f1baa30fd5/exceptions.json`；摘要见`artifacts/validation/00928-diagnosis.json`。

## 代码完成

- 增加可返回正常缺失的路径核验，保留根目录身份、当前用户归属、路径边界及重解析点保护；从根向目标检查，不穿越外部链接。公开ValidateOperation继续拒绝缺失路径，权限等异常继续报错。
- 发布前核实目标。已解决且文件已不存在时，仅在上传代次及完整问题记录仍匹配后结束属性清理；不调用Shell、不修改文件、文件戳、上传结果或内容基线。未解决的缺失文件继续保留问题和原因，并记录提示未发布。
- 发布与清除失败均按捕获的代次和问题记录退避30秒。问题变化、成功或取消可立即重新处理；只接收匹配本次捕获记录或本次发布日志的失败，迟到错误不覆盖新问题。
- 版本、程序集、清单统一0.0.9.28。所有产品及文档改动由版本补丁经git apply --check后应用；本轮最小编译采用独立非打包Debug输出，未覆盖当时仍运行的原调试输出。

## 实际验证

| 项目 | 结果 | artifacts/validation证据 |
| --- | --- | --- |
| x64 Debug主工程最小编译 | 非打包独立输出通过 | 00928-minimal.log |
| x64 Debug测试工程编译 | 0警告、0错误 | 00928-tests-minimal.log |
| 针对性回归 | 95/95，失败0、跳过0 | 00928-targeted.log／trx |
| x64 Debug全量 | 1096/1096，失败0、跳过0 | 00928-debug.log／trx |
| x64 Release全量 | 1096/1096，失败0、跳过0 | 00928-release.log／trx |
| x64／ARM64签名MSIX | 签名、架构及内容核验通过 | 00928-release.log、00928-arm64-package.log |
| ARM64测试 | 仅交叉编译通过 | 00928-arm64-tests-build.log |
| x64非打包Release | 构建通过，未启动 | 00928-unpackaged.log |
| 正式0.0.9.28运行复核 | 连续70秒CLR异常0；已取消条目自动Cleared | 00928-runtime-proof.json、00928-runtime-trace.json |
| 实际Compile登记 | 主188／测试253，相关文件各一次、无重复遗漏 | 00928-compile-main.json、00928-compile-tests.json |

新增12项隔离NTFS／CFAPI回归覆盖ClearPending／Clearing、文件及父目录删除、无首次抛出的正常缺失、持久化后重新读取、未解决问题保留、取消立即收尾、两个发布方向的退避、同代及新代问题的迟到失败、根身份／越界／外部联接保护。缺失收尾前后比较完整上传字段、其他占位文件状态戳和本地DAV请求计数；远端请求没有增加。完整回归继续覆盖读取、上传恢复、删除、固定及已有属性写入链。

## 产物

| 架构 | 签名MSIX | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.28_x64_Test/HybridMount_0.0.9.28_x64.msix | A1862A6C48FE57B835175DBECDD795FCA5228C3137316F5A0AF9D457F759DC02 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.28_arm64_Test/HybridMount_0.0.9.28_arm64.msix | 4E32B9E4F58C2DAF28D88FDCF974EEF690306424005514853EB00B6C9CE76EDF |

## 新版正式运行复核

收尾时检测到原PID 19760已退出，09:20:33启动了新PID 24072，同一路径的程序集版本及09:20:34启动日志均为0.0.9.28；本轮工具未启动、结束或重启该程序。只读复核原记录：State仍为Canceled、Generation仍为1、ResolvedUtc保留原值，ShellState已由程序自动从ClearPending收尾为Cleared，ShellError清空，原文件仍不存在。

对新进程再做70秒相同CLR采样，捕获异常总数0，FileNotFoundException及DirectoryNotFoundException均为0，进程保持响应。这个结果验证了正式运行中的缺失提示循环已收尾，不将有限采样推论为全部启动或使用场景都无异常。机器可读证据为`artifacts/validation/00928-runtime-proof.json`。

## 待测试及边界

正式0.0.9.28的缺失条目及持续运行采样已按上节实测；未为验证重建同步根或手改正式上传日志。GUI／Explorer目视、MSIX安装升级、生成的非打包Release启动及ARM64实机尚未执行。既有Shell及其他审查待办继续单列。

自动回归仅使用本地HTTP夹具和隔离NTFS／CFAPI根；另对既有正式进程执行上述只读复核。之前同轮服务器能力查询仅发送只读请求，未写入远端；未访问已取消NAS。当前D:\myweb的Apache端点不支持RFC6578（合法REPORT返回501）及所查询的配额属性（属性404），与本次本地提示清理错误分别记录，结果见`artifacts/validation/myweb-server-capabilities-20261107.json`。

独立本地提交；本次未推送GitHub或Azure。机器可读验证摘要见`artifacts/validation/00928-evidence.json`。

# HybridMount 0.0.9.4 验收记录

2026-11-06，Windows x64。开发与自动验证预算4–8小时，桌面／Explorer及外部等待另计。本版在0.0.9.3本地提交713ecca之后独立实施；仅本地提交，不推送。

## 代码完成

- 三个工程和现有SDK保持，产品、程序集、原生扩展及MSIX统一0.0.9.4，IPC仍v2，SQLite仍v6；定义和备份格式不变。全部非二进制目标修改先编写补丁，通过`git apply --check`后应用并删除补丁。
- 上传JSON新增可选UploadAttention，保存原因、阶段、时间、代次、当前失败／重试状态及Shell发布／清除日志。首次失败不要求索引或历史；历史清理不影响问题，启动补齐旧记录。Completed、Canceled及明确取消的结果不重建旧错误；已发送写入取消继续保留未知结果证据。
- 重试不清空上次原因，只有实际重试状态显示“正在重试”；重新失败产生新事件。容量阻挡和发生错误的本地提交不自动再次调度，需显式重试；未知结果的后台核对只读，确认尚为原基线后等待显式重试，已确认本次内容成功可继续提交。
- 挂载卡片诊断按钮数量徽标及Tooltip、固定32 DIP第三行；有问题时点击定位任务页，右键保留完整诊断。任务页独立问题视图、挂载／路径过滤、选中重试、详情及打开所在位置；页面不互相调用ViewModel，定位服务保持固定`hybridmount:///tasks`路由。
- 新问题每挂载合并为可点击的应用内InfoBar，启动时已有问题只恢复计数；关闭提示不解除问题，不发系统通知。详情包含Shell发布错误，完整诊断列出没有索引、文件已缺失以及同一路径多个未解决上传的问题。
- Explorer使用本产品自定义属性ID=1及自绘警示PNG，采用StorageProviderItemProperties.SetAsync。同一旧根原位添加定义，不注销或重建根。原生命令新增“查看同步问题”，IPC v2增加showSyncIssues；仅验证归属并传递定位，不访问网络、不创建上传或任务。

## Explorer属性安全验证

在本地HTTP夹具与真实NTFS／CFAPI隔离根完成，使用最终生产发布链：

- 普通本地文件、冷占位、固定文件、脏占位及目录的发布／清除不读取正文，不改变文件／占位身份、长度、写入时间、InSync、固定状态、已缓存字节或上传代次；请求计数保持不变。
- 原只读CFAPI打开方式会水合冷占位，本轮技术验证实际复现，未沿用该方式。最终使用原生属性访问加DELETE访问权并仅共享读取；DELETE访问权只用于Windows共享检查，不执行删除。纯属性访问会忽略共享限制，加入该访问权后外部写入和改名均被拒绝，且冷占位仍为0缓存字节。
- 在原生句柄仍有效时生成USN close记录并读取最终状态戳；同代次、身份及原观察／快照戳匹配才提交，保护释放后不重新读取并吸收编辑。准确安排保护释放后写入B，后续扫描能建立新代次。
- 属性写入前保存Publishing／Clearing及原状态戳；模拟属性已写、数据库确认前中断，重启重放不因元数据增加上传代次。若中断后发生真实编辑，仍建立新代次。身份被替换时不改新文件属性，Shell错误不改变原上传结果。
- 旧描述符不含问题定义时，生产原位注册补充后根标记和文件身份不变。同一路径原上传／副本问题聚合，完成一项不会清除其他未解决问题。

以上为API、原生及文件级安全验收。Explorer实际图标、文字和右键交互目视尚未执行，不以IPC接受或右键入口代替此项。

## 自动验证及实际启动

| 项目 | 实际结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| 最小WinUI | x64 Debug非打包编译通过 | 0094-winui-minimal2.log；最终Debug构建也通过 |
| 状态／问题／属性针对性 | 最终Release 34/34 | 0094-release-issue-retest.log／trx |
| x64 Debug完整 | 573/573，0失败／跳过 | 0094-debug-final.log／trx |
| x64 Release完整 | 573/573，0失败／跳过 | 0094-release-final.log／trx |
| 原生菜单／容量／属性 | 20/20；完整回归含相同用例 | 0094-capacity-shell-retest.log／trx |
| 双协议受控HTTP闭环 | WebDAV、S3各自实际HTTP拒绝→保留问题→显式重试→再次失败→成功清除；没有自动重发 | 0094-http-issue-flow.log／trx及最终全量 |
| 非打包实际启动 | 静默启动、静默第二实例、正常第二实例恢复窗口、挂载外两个缓存请求拒绝通过；应用留存运行 | 0094-unpackaged-launch.log |
| 运行中问题IPC | 挂载内定位accepted，越界D:\请求rejected，不生成jobId；导航错误0 | 0094-running-ipc-focus.json、0094-running-ipc-outside.json、0094-final-evidence.json |
| MSIX | x64／ARM64签名验证0错误／警告，版本、架构、自包含、处理器和SyncIssue.png均通过 | 0094-release-final.log、0094-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，0警告／错误 | 0094-arm64-tests-build.log；未在ARM64运行 |
| 编译／格式 | 主工程177、测试219项Compile，无重复；中央依赖、global.json及MountDatabase.cs与HEAD一致 | 0094-structure-before-launch.json |

首轮证据保留：旧历史清理断言把新增兼容问题字段也计入不变记录，改为比较保存后的证据；属性探索复现直接更新的USN和水合副作用，最终生产安全回归已通过。中断日志首轮空快照分支错误修复后复测通过。首轮Release为571/572，一项断言在失败已落库、工作器尚未释放时抢先要求属性Published；生产正确推迟发布。测试改为有界等待发布／清除完成后，34/34及最终Debug／Release各573/573通过，未放宽安全断言。早期失败日志不覆盖或改记为通过。

## 正式数据和桌面边界

- 启动前后D:\myweb、D:\s3test的卷／文件ID、挂载ID及原生根身份摘要一致；三个正式挂载定义的摘要一致。三项SHX均为Completed，EXX11901.shx代次3，FSD.SHX和MON.SHX代次2，未代为重试或恢复旧错误。见0094-formal-identities-before.json／after-start.json和0094-definitions-before-launch.json／after-launch.json。
- 启动前只读SQLite v6中有13项Completed、1项Canceled、1项旧OutcomeUnknown；旧记录是否构成当前问题由状态恢复和只读核对决定，不能因为没有历史就忽略。未清空正式任务或重建正式根，不复制打包／非打包配置。
- 非打包0.0.9.4实际会话PID 10736、Ready已记录，当前保留运行供用户手动检查；活动会话的Completed=false表示尚未退出，不作为异常退出证据。没有强杀进程或重启Explorer。
- computer-use初始化和重置后重试均失败：`windows sandbox failed: helper_unknown_error: setup refresh had errors`。未绕过工具控制桌面。GUI卡片行高、徽标、Tooltip、通知、筛选及Explorer实际显示继续待测。
- 系统Appx模块查询当前用户HybridMount包数量为0（0094-installed-before.json），本轮仅生成并核验签名包，未执行安装或升级；不能沿用0.0.9.3历史安装状态作为当前通过。安装升级、ARM64实机及真实服务故障／长测继续待验收。
- 本轮故障注入仅使用本地隔离DAV／S3服务；没有在真实远端创建回归目录或测试文件，也未访问已取消的10.0.0.20:5007。正式挂载正常启动恢复不作为真实服务兼容性认证。历史SHELL-01及其他审查待办保持。

## 产物

签名证书：3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

| 架构 | 产物 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.4_x64_Test/HybridMount_0.0.9.4_x64.msix | 1759DBB4D1FB9369277969C77B7AB859B4525F49B327FC7CB6EE509E0179BD28 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.4_arm64_Test/HybridMount_0.0.9.4_arm64.msix | 5B64B6C1DBF051F7AE8C6F6C4BC8FB3EEB52B0D297A08B6A652CDD8B4B658F9B |

可复跑入口：build/Build.ps1、build/Test-Launch.ps1、build/Invoke-Request.ps1。默认只做本地提交；本版不推送。

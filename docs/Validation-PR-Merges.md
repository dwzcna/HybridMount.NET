# GitHub PR逐版合并记录

2026-11-06，单执行者按#1→#15逐个合并到master，0.0.9.6→0.0.9.20；每次一个真实双父合并提交，只本地提交，不推送。开发及自动验证预算2–4小时，真实服务及桌面／实机等待另计。

三方合并先在Git对象树预演，生成patch/补丁，经git apply --check后应用；有冲突时先在对象树核对解决，再应用受检补丁。独立版本记录后完成合并提交，保留PR来源父节点。

| PR | 版本 | 内容 | 针对性 | Debug | Release | 双架构包／ARM64交叉编译 |
| --- | --- | --- | --- | --- | --- | --- |
| [#1](https://github.com/dwzcna/HybridMount.NET/pull/1) | 0.0.9.6 | 发送前错误分类与条件写有限重试 | 52 | 617 | 617 | 通过／仅交叉编译 |
| [#2](https://github.com/dwzcna/HybridMount.NET/pull/2) | 0.0.9.7 | 上传指数退避与Retry-After | 76 | 653 | 653 | 通过／仅交叉编译 |
| [#3](https://github.com/dwzcna/HybridMount.NET/pull/3) | 0.0.9.8 | S3只读请求有限重试 | 56 | 681 | 681 | 通过／仅交叉编译 |
| [#4](https://github.com/dwzcna/HybridMount.NET/pull/4) | 0.0.9.9 | 远端区间单请求流式读取 | 32 | 701 | 701 | 通过／仅交叉编译 |
| [#5](https://github.com/dwzcna/HybridMount.NET/pull/5) | 0.0.9.10 | S3目录枚举效率与坏条目隔离 | 51 | 719 | 719 | 通过／仅交叉编译 |
| [#6](https://github.com/dwzcna/HybridMount.NET/pull/6) | 0.0.9.11 | S3分片条件完成与条件PUT回退 | 20 | 729 | 729 | 通过／仅交叉编译 |
| [#7](https://github.com/dwzcna/HybridMount.NET/pull/7) | 0.0.9.12 | S3分片日志与隔离恢复 | 41 | 748 | 748 | 通过／仅交叉编译 |
| [#8](https://github.com/dwzcna/HybridMount.NET/pull/8) | 0.0.9.13 | WebDAV临时资源元数据清理 | 35 | 764 | 764 | 通过／仅交叉编译 |
| [#9](https://github.com/dwzcna/HybridMount.NET/pull/9) | 0.0.9.14 | WebDAV加锁更新减少重复传输 | 31 | 783 | 783 | 通过／仅交叉编译 |
| [#10](https://github.com/dwzcna/HybridMount.NET/pull/10) | 0.0.9.15 | 上传准备与父目录操作移出根锁 | 125 | 812 | 812 | 通过／仅交叉编译 |
| [#11](https://github.com/dwzcna/HybridMount.NET/pull/11) | 0.0.9.16 | 本地上传索引与调度唤醒 | 88 | 826 | 826 | 通过／仅交叉编译 |
| [#12](https://github.com/dwzcna/HybridMount.NET/pull/12) | 0.0.9.17 | Watcher增量扫描与全量兜底 | 114 | 852 | 852 | 通过／仅交叉编译 |
| [#13](https://github.com/dwzcna/HybridMount.NET/pull/13) | 0.0.9.18 | S3移动删除的语义版本比较 | 58 | 880 | 880 | 通过／仅交叉编译 |
| [#14](https://github.com/dwzcna/HybridMount.NET/pull/14) | 0.0.9.19 | S3目录幂等移动与部分清理结果 | 93 | 915 | 915 | 通过／仅交叉编译 |
| [#15](https://github.com/dwzcna/HybridMount.NET/pull/15) | 0.0.9.20 | S3大对象分片复制与元数据保留 | 89 | 940 | 940 | 通过／仅交叉编译 |

## 批次收尾

15个来源分支按#1至#15逐个合入本地master，每个PR保留独立双父合并及一次第四段升版，0.0.9.6至0.0.9.20连续。所有版本分别完成最小x64编译、针对性、Debug／Release完整回归、x64／ARM64签名MSIX及ARM64测试交叉编译，实际计数见上表及逐版记录。最后一版针对性89/89、Debug和Release各940/940；签名包SHA256见Validation-0.0.9.20.md。

最终x64非打包Release构建通过且未启动，EXE与程序集均为0.0.9.20；主工程182／测试240个实际编译项无重复，本批相关28／52文件无遗漏。提交后版本链、父节点、来源祖先、工作区及全部包摘要核对证据保存为artifacts/validation/pr-merges-final-audit.json。三个工程、正式配置及同步根保全，本批未切换运行程序、安装或推送，也未访问已取消NAS。

PR #7没有可靠UploadId的意图保留待核对，自动精确回收仍未实现；不允许按键名和时间猜测并中止外部上传。真实服务、GUI／Explorer、安装升级及ARM64实机均未执行。大对象分片复制以本地协议夹具验证，不作为真实5 GiB传输或兼容性认证。

## 第二批：PR #16—#18

2026-11-06，从0.0.9.20继续逐个拉取、合并及单次递增第四段版本，预计开发及自动验证45–75分钟。已合入本地和GitHub master的旧15个本地／GitHub来源分支精确清理，提交仍保留在master；证据见artifacts/validation/pr01-15-local-branch-cleanup.json及pr01-15-github-branch-cleanup.json。主分支未推送，正式配置和运行模式不切换。

| PR | 版本 | 内容 | 针对性 | Debug | Release | 双架构包／ARM64交叉编译 |
| --- | --- | --- | --- | --- | --- | --- |
| [#16](https://github.com/dwzcna/HybridMount.NET/pull/16) | 0.0.9.21 | 协议探测作用域与权限回退 | 150 | 991 | 991 | 通过／仅交叉编译 |
| [#17](https://github.com/dwzcna/HybridMount.NET/pull/17) | 0.0.9.22 | 暂存磁盘空间检查 | 115 | 1010 | 1010 | 通过／仅交叉编译 |
| [#18](https://github.com/dwzcna/HybridMount.NET/pull/18) | 0.0.9.23 | 冲突副本使用不可变快照 | 92 | 1033 | 1033 | 通过／仅交叉编译 |

## 第二批收尾与PR #16更新

PR #16来源a0911b2回并旧master，产品实现已在0.0.9.21合入；新测试等待与当前Drain适配器等价，解决冲突后完整代码／测试树与0.0.9.23一致。保留当前上传及移动后等待，不引入新的产品或测试改动；本次仍独立合并并递增第四段版本。

| 更新 | 版本 | 针对性 | Debug／Release | 包及构建 |
| --- | --- | --- | --- | --- |
| [PR #16更新](https://github.com/dwzcna/HybridMount.NET/pull/16)，a0911b2 | 0.0.9.24 | 51/51 | 本版编译通过；全量未重复，0.0.9.23各1033/1033保留为上版证据 | 双架构签名包、ARM64测试交叉编译、x64非打包通过 |

累计18个PR、19次真实双父合并和连续第四段升版0.0.9.6—0.0.9.24；PR #16两次来源均保留，不将更新伪称新PR。最终实际Compile项主工程186、无头测试248，41／68个本批相关文件无遗漏及重复；WinUI页面仅纳入主工程。最终非打包EXE和程序集均为0.0.9.24，仅构建未启动或安装。版本链、来源祖先、干净工作区和包摘要核验见artifacts/validation/pr-merges-final-audit-round2.json，逐版结果及限制见Validation-0.0.9.24.md。

本地来源分支逐个合并后删除；GitHub上已发布旧15个来源已按精确SHA租约删除，#16—#18三个新来源暂留至master发布。当前GitHub master仍为807d554；本批未推送master，也不推Azure origin。GUI／Explorer、真实服务、安装升级及ARM64实机未执行；PR7缺少可靠UploadId时的自动精确回收仍未实现。

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

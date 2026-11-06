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

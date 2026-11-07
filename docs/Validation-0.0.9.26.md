# 0.0.9.26：PR #19合并验收

日期：2026-11-07。按用户要求逐个拉取、逐个合并到master，每次递增第四段版本。本版来源为[上传提交恢复哈希移出挂载锁并校验提交代次](https://github.com/dwzcna/HybridMount.NET/pull/19)，精确提交0491d6fca274a6d0c341e911961a3ccfab22880c，合并前master为814701e2f0f4c6bb7b311a87d65900dfe6e0ec0b。

## 代码完成

远端已确认、本地原生提交后的恢复核验不再持有根锁读取整文件。短锁捕获上传记录和文件戳，锁外异步计算SHA256，再核对挂载定义、会话、源、记录、索引及文件状态；原生保护句柄内继续重验已验证戳。仓库提交比较完整捕获记录，迟到失败不能覆盖新代次或取消状态。

来源先经Git对象树三方合并预演，差异生成版本补丁，经git apply --check后应用；提交保留来源第二父节点。版本、程序集和MSIX统一0.0.9.26。必要中文职责说明随本版补齐，不增加参考仓库说明；保留三个工程、现有SDK、IPC v2、SQLite及配置格式。

## 实际验证

| 项目 | 结果 | artifacts/validation证据 |
| --- | --- | --- |
| x64 Debug最小编译 | 通过 | pr19-minimal.log |
| 针对性回归 | 110/110，失败0、跳过0 | pr19-targeted.log／trx |
| x64 Debug全量 | 1052/1052，失败0、跳过0 | pr19-debug.log／trx |
| x64 Release全量 | 1052/1052，失败0、跳过0 | pr19-release.log／trx |
| x64／ARM64签名MSIX | 签名及内容核验通过 | pr19-release.log、pr19-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，未实机运行 | pr19-arm64-tests-build.log |
| x64非打包Release | 构建通过，未启动／安装 | pr19-unpackaged.log |
| 实际Compile登记 | 主186／测试249，相关文件无重复遗漏 | pr19-compile-main.json、pr19-compile-tests.json、pr19-evidence.json |

针对性及全量结果均为本版实际执行，未将来源PR描述或历史结果记为测试通过。

## 产物

| 架构 | 签名MSIX | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.26_x64_Test/HybridMount_0.0.9.26_x64.msix | 1528DD1FA987646F16FC39041BD66CCC2F2B1D5FBD477211EE7F89A7E841FA41 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.26_arm64_Test/HybridMount_0.0.9.26_arm64.msix | 8487D162D2310FC13AF716CC198268C1187B07ACB63180A5B4F8DF7341A56622 |

## 限制与发布

自动测试仅使用本地服务及隔离NTFS／CFAPI根，未访问已取消NAS、未连接真实WebDAV或S3、未加载或修改正式配置及同步根。GUI／Explorer、安装升级和ARM64实机未执行；历史Shell和审查待办继续保留。

本版独立双父合并提交的父节点、来源祖先及版本由本批审计核对；全部PR完成后master普通推送GitHub和Azure，再按精确SHA逐个删除已经发布合并的远程来源。发生来源更新时不删除旧清单对应的新提交。发布结果保存在artifacts/validation/pr-round3-publication.json。

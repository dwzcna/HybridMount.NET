# HybridMount 0.0.5 验收记录

日期：2026-10-30，Windows x64，.NET SDK 10.0.401。产品 0.0.5，程序集和 MSIX 0.0.5.0。

## 已完成

- 持续上传、旧配置／旧改动迁移；生产开关及首次选择对话框移除，单项取消、冲突、结果不明、远端删除和本地删除规则保留。
- WebDAV 精确临时项意图日志、内容校验清理及重连恢复；只读操作不写入、不建立辅助目录，安全写入策略保留。SQLite v4，注册表定义版本 6。

## 未完成

0.0.5 范围内无已知待实现代码。缩略图与退出取消修复分别在 0.0.5.1／0.0.5.2 实施。

## 待测试

- 实际 GUI／Explorer、安装升级、ARM64 实机及长期断网压力。非打包测试以精确归属进程受控中断收尾，不代表正常托盘退出已通过。
- 0.0.4.3 精确历史残留路径仍返回 HTTP 409；只清理已知清单，不按名称批量删除。其内容确认为原探测字节，详见 005-historical-cleanup.json 和此前 0.0.4.3 验收记录。

## 已测试

证据在 artifacts/validation，脚本、产物和本机凭据不提交。

| 范围 | 实际结果与证据 |
|---|---|
| Debug／Release | x64 各 158/158，零失败／跳过；005-all-x64-*.trx |
| 新行为 | 旧关闭状态、Ignored 与旧新增自动上传，已取消同代不恢复；临时项持久恢复不重传用户文件，外部内容／目录／另一账号保护，创建已写但响应取消可独立清理 |
| 坚果云 | 既有“我的坚果云”内唯一文件，文件系统触发上传、读回 SHA-256、临时项为零、测试文件和隔离根清理成功 |
| R2 | 既有桶前缀内唯一文件，文件系统触发上传、SHA-256 读回、测试文件和隔离根清理成功 |
| 真实测试范围 | 005-real-upload.json；原配置、根位置未改绑，未建立远端测试目录 |
| 非打包启动 | Ready、重复实例、恢复窗口、非挂载拒绝通过；005-launch-x64-Unpackaged.*，本轮测试进程已精确结束 |
| 双架构签名包 | 自包含、架构、版本、硬盘图形及扩展声明通过；005-signature-*.log |

构建命令：

```powershell
./build/Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged -Test
./build/Build.ps1 -Platform x64 -Configuration Release -Deployment Packaged -Package -Test -CertificateThumbprint '<当前用户证书指纹>'
./build/Build.ps1 -Platform ARM64 -Configuration Release -Deployment Packaged -Package -CertificateThumbprint '<当前用户证书指纹>'
```

| 架构 | 签名包 | SHA-256 |
|---|---|---|
| x64 | artifacts\packages\x64\HybridMount_0.0.5.0_x64_Test\HybridMount_0.0.5.0_x64.msix | 64FB692A8441E5E72B731851E510D91922EA89A37D44E263E34BE8D78BB7AA5C |
| ARM64 | artifacts\packages\ARM64\HybridMount_0.0.5.0_arm64_Test\HybridMount_0.0.5.0_arm64.msix | B85B946B2764E161662793D7ED4AA347CF24D86C36A192FBE7BD45A7BCDA24E0 |

使用现有开发证书，签名无时间戳。ARM64 为交叉构建，不代表实机测试通过。

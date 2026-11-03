# HybridMount 0.0.8.2 验收记录

2026-11-04，Windows x64，.NET SDK 10.0.401／VS 2026／Windows SDK 26100。三工程、程序集与 MSIX 统一 0.0.8.2，数据库 v6、备份 v3 与 IPC v2 不变；0.0.8.1／2 合计预算 90–150 分钟，GUI 等待另计。

## 已完成

- 任务按执行中→排队／等待→历史显示，组内按首次 SQLite 行顺序，进度不参与排序；历史 100 条与当前任务分开，调度优先级不变，增量刷新保留选择／滚动。
- WebDAV RFC 4331 只读配额与范围说明、部分未知及溢出保护；S3 明确服务未提供。容量为服务器针对挂载根返回的账户／目录配额，非 NAS 物理容量。
- 五分钟缓存／在途共享、独立等待取消、失败旧值及时间、连接代次与生命周期；手动刷新／重连更新，选择不写设置。

## 未完成

本版代码范围已实现。真实 WinUI 与设备验收未执行，不能用模型回归代替实际滚动和控件行为。

## 已测试

| 范围 | 实际结果与证据（artifacts/validation） |
| --- | --- |
| 针对性／编译 | 18/18：0082-capacity-order.trx／0082-capacity-order.log；0082-minimal-fixed.log、最终 Debug／Release 编译通过 |
| 完整 x64 | Debug／Release 各 367/367，0 失败、0 跳过：0082-contracts-x64-Debug-final.trx／0082-contracts-x64-Release.trx；0082-debug-final-cache.log／0082-release-final.log |
| 原生／驱动回归 | 完整回归包含现有 CFAPI、上传、远端更新、删除、退出、SQS、共享下载和预览；新增配额取消及正文期限、150 条历史、稳定提交顺序，不改变队列发送顺序 |
| 真实坚果云只读 | 0082-real-quota-79776d21c2f04caa91a5dc66ac23e796/report.json、0082-quota-live.log。未返回可用／已用属性，三项为未知；无远端写入、同步根注册、正式数据库打开，不把未知记成数值容量认证 |
| 双架构 MSIX | 0082-package-x64-final.log／0082-package-ARM64-final.log，签名、清单／PE／依赖／许可／资源、自包含及缩略图门控核验通过，0.0.8.2 |

针对性覆盖：有效及零值、单项缺失、属性级 404、负／无限／畸形／溢出、403、越界 href、正文超时与调用方取消；五分钟缓存、同请求合并、一个等待者取消、失败旧值／时间不变、停机等待忽略取消的旧结果并丢弃；S3 不发请求，不以本地容量替代；当前任务超过历史窗口仍显示，字节进度更新不变序。

初轮 Debug 通过后补齐选择读取缓存入口，最终 Debug 重新全量通过，旧轮保存在 0082-contracts-x64-Debug-before-selection-cache.trx；最终 Release 程序集和包构建晚于该修改。

产物：artifacts/packages/x64/HybridMount_0.0.8.2_x64_Test/HybridMount_0.0.8.2_x64.msix；ARM64 对应目录。签名证书 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

## 待测试

- Computer Use 的 sandbox helper 初始化仍失败，实际任务滚动／选择／状态转组和配额控件、窄窗／高缩放／高对比度、Explorer、安装升级及 ARM64 实机未执行。
- 本版非打包启动未执行，继续保留用户新增 D:\myweb 自动连接未注册定义及现场，不改正式 AutoConnect。既有编译、CFAPI 测试和 0.0.7 启动不能代替本版启动。
- 支持 RFC 4331 实际数值的真实服务认证；当前坚果云只确认缺失属性兼容。NAS 测试保持取消，真实 AWS SQS 和历史 SHELL-01 按原边界保留。

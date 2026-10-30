# HybridMount 0.0.5.2 验收记录

日期：2026-10-31，Windows x64，.NET SDK 10.0.401。产品、程序集及 MSIX 均为 0.0.5.2。退出修复开发及自动验证预算 2–4 小时，外部服务和桌面等待另计。

## 已完成

- 连接、启动恢复和表单只读测试归属应用，停止后拒绝新连接／上传／任务／预览；重复退出及并发释放共用同一任务。
- 退出先同时取消连接、读取、上传、预览和维护，再等启动及手动操作结束；迟到工厂结果清理一次，不注册／开启新会话，等待不持有完成所需根锁。
- 关联令牌取消与内部 HTTP 超时分开；停止的读取不产生失败任务或挂载异常，已发送写入保留 OutcomeUnknown／不可变快照，重启先核对。
- CFAPI 先结束回调和通知再断开；关闭时按原请求范围结束，只有关联取消时接受系统已取消的请求终态；常规退出保留同步根。
- 托盘 async void 有边界保护，清理逐阶段独立执行；真实错误继续清理其他资源并记录 ExitCompletedWithErrors，不标为全部成功。日志在生产者停止后排空，随后释放窗口／托盘／单实例。
- 生命周期记录实际包身份、调试器状态、停止阶段及 Shutdown／Disconnect／Caller／Removal 取消原因；不保存原始异常消息、响应或凭据。
- 普通缓存缺失用可空原生元数据查询；通知、令牌及客户端按工作结束顺序释放。移除新增挂载对话框遗留的无效上传开关，持续自动上传行为一致。

## 未完成

本批代码范围内无已知待实现内容。Explorer 正式缩略图注册仍由真实入口验收门槛控制，实验包继续提供。

## 待测试

- 实际托盘连续点击退出、调试器附加时的完整 WinUI／Explorer 交互、正常产品进程退出码 0。自动化验证了同一生产服务的停止路径，但不能替代真实托盘输入。
- 包身份下的退出时序、安装升级、ARM64 实机及长时间断网／大对象／系统解码器组合压力。
- 桌面工具初始化与重置重试失败，沿用 0051-gui-attempt.txt 证据；没有执行 GUI 输入，也没有用其他输入方式替代。
- 此前未知 0xffffffff 的历史原因不据本轮结果反推。最新用户托盘记录已有 ExitCompleted；本次处理的是取消分类和完整释放。
- VS“引发时中断”仍可能停在 HTTP／SDK 自己抛出并捕获的取消异常；继续运行后的未处理异常、任务状态与生命周期是判断依据。
- 历史坚果云探测文件的精确 DELETE 409 记录继续保留，没有按名称前缀扩大清理。

## 已测试

证据在 artifacts/validation；本机脚本、产物和凭据不提交。

| 范围 | 结果及证据 |
|---|---|
| x64 Debug／Release | 各 190/190，无失败／跳过；0052-all-x64-*.trx |
| 新增退出测试 | 17 项：真实 S3 SDK 阻塞探测／枚举／水合／预览／表单测试，启动即停止、手动／调用方断开、重复释放、所有连接先收到取消、迟到非协作工厂、部分管道请求、缓存缺失、超时保留故障、阶段失败继续清理 |
| 写入未知及重启 | 文件系统触发 S3 PUT；分别模拟服务已写／未写但响应未返回，停止保留 OutcomeUnknown；重建后已写核对成功、未写要求显式处理，均不重复 PUT |
| 受控停止耗时 | Debug／Release 最大 2013ms，均低于 5 秒；0052-shutdown-metrics.json。测量不含调试器暂停，不通过提前释放／强杀实现 |
| 真实只读启动即停 | 现有坚果云／R2 配置在隔离根启动连接，停止分别 17ms／7ms，无错误日志、无残留注册／远端写入；0052-real-stop.json |
| 真实上传回归 | 两服务文件系统触发上传、读回 SHA-256、精确清理本轮文件和隔离根，DAV 临时项为零，未建远端目录；0052-real-upload.json |
| 非打包运行 | Ready、静默／重复实例、恢复窗口及非挂载拒绝通过；0052-launch-x64-Unpackaged.*。本轮原始进程对象受控中断，不作为正常托盘退出证据 |
| 最终表单 | 删除遗留开关后 x64 Debug 最小编译及两架构最终打包编译通过；网络／挂载测试输入未改变，未重复协议全套 |
| 双架构签名包 | x64／ARM64 正式及实验 MSIX 的版本、架构、自包含、资源、声明和签名检查通过；0052-signature-*、0052-preview-signature-* |

首轮新增退出测试发现关闭后的 CFAPI 回调使用空范围返回错误状态，产生云操作无效日志；修正范围与取消完成分类后，17 项新增测试及最终全套通过。真实故障仍保留记录，未扩大异常过滤范围。

```powershell
./build/Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged -Test
./build/Build.ps1 -Platform x64 -Configuration Release -Package -Test -CertificateThumbprint '<当前用户证书指纹>'
./build/Build.ps1 -Platform ARM64 -Configuration Release -Package -CertificateThumbprint '<当前用户证书指纹>'
# 正式包完成后，同架构串行构建实验缩略图包
./build/Build.ps1 -Platform x64 -Configuration Release -Package -ExperimentalThumbnails -CertificateThumbprint '<当前用户证书指纹>'
./build/Build.ps1 -Platform ARM64 -Configuration Release -Package -ExperimentalThumbnails -CertificateThumbprint '<当前用户证书指纹>'
```

| 架构／模式 | 签名包 | SHA-256 |
|---|---|---|
| x64／正式 | artifacts/packages/x64/HybridMount_0.0.5.2_x64_Test/HybridMount_0.0.5.2_x64.msix | EB3A2AA4E80A0F0667601B83EF49146558C2F5E665E5DC8A733C336637B84E90 |
| ARM64／正式 | artifacts/packages/ARM64/HybridMount_0.0.5.2_arm64_Test/HybridMount_0.0.5.2_arm64.msix | B3E672741B050D5582FD4A7CA850CB2299829D4DC6C5276F248FB90ED8E5E2A0 |
| x64／实验 | artifacts/packages-preview/x64/HybridMount_0.0.5.2_x64_Test/HybridMount_0.0.5.2_x64.msix | FFF2669FAB1CF6CB5D7819CC8B36BF7DCFC5CA461DB076C88BA5030EDD81CA54 |
| ARM64／实验 | artifacts/packages-preview/ARM64/HybridMount_0.0.5.2_arm64_Test/HybridMount_0.0.5.2_arm64.msix | AF0436B316D4B5C2CF84410B810002415A85C3B158570057294118DD0D498690 |

本机开发证书签名，无时间戳；ARM64 为交叉构建。正式包不自动注册实验缩略图处理器，实验包仍需每挂载开启。

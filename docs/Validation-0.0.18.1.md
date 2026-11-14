# 0.0.18.1 验证记录

基于9c380a9，保留四工程、产品身份与存储格式；不访问生产私网，不读取开发机应用日志代替生产证据，不操作正式挂载或Explorer。范围及预计工期见专项计划。

## 实现及实际证据

证据目录artifacts/validation/00181-work/。

- WebDAV实际TCP按族连接，IPv4映射归一化、域名IPv4优先及250毫秒延后IPv6、最多两个候选；DNS／竞争共享期限，胜出流转交连接池，落败Socket取消并排空。代理和TLS继续由SocketsHttpHandler处理。
- 请求级阶段及证书归属；系统验证成功接受，失败仅预期主机和当前挂载精确指纹。只读测试先根集合，后可选OPTIONS，共享120秒总期限。
- 独立获取不接收凭据或旧pin，在TLS捕获叶证书后拒绝握手。UI直接填框，忙时互斥、关闭取消、配置代次隔离。
- 首轮测试为SafeSocketHandle命名空间编译问题，修正后28项中27通过；旧TLS用例仍期待OPTIONS先行，按新顺序更正后28/28。后补获取零HTTP、并发证书隔离、OPTIONS受限和总期限；最终Debug／Release各42/42、零失败零跳过，见transport-debug-final.trx、transport-release-final.trx。
- x64 Debug非打包主程序编译通过，三语言1243键；证据build-x64-debug.log。

## 待验证

完整产品WinUI输入／关闭／窄窗与语言交互，Easy Connect生产连接／列目录／读取、实际系统代理CONNECT、TLS重协商、连接池长时复用、Windows11 21H2与ARM64实机未执行。现有隔离TLS不代表这些场景通过。

生产用户需在Easy Connect下获取填框、测试、列目录及读取，确认实际纯IPv4路径。未更改正式链／吊销策略，未安装系统证书信任。

## 交付

双架构Release自包含签名MSIX、signtool验签及包内容检查通过，见package-x64.log、package-arm64.log。ARM64测试交叉编译0警告／0错误，见tests-arm64-build.log；未执行ARM64测试。包未安装。SHA256见packages.sha256.json。版本化补丁保存后本地提交，不推送。

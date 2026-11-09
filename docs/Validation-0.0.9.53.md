# 0.0.9.53 验收记录

基线ea6c254（.52），继续codex/native-backend／NativePreview，受检补丁本地实施。

## 实现

新导出使用.hymcfg，导入接受.hymcfg和.hmconfig。空密码以空字节串进入原PBKDF2-SHA256（600000次）／AES-GCM；HMCONFIG头、44字节认证头、16字节tag、盐／nonce和Schema v5均不变，兼容v1—v4。非空密码至少8字符且不修剪，空密码仍需确认字段一致；界面注明空密码无密码保护。

## 实际验证

证据artifacts/validation/00953-backup/：

- x64 Debug非打包最小构建通过（minimal-build.log）。
- 同步重建两配置隔离原生测试映像；相关Debug／Release各78/78、0跳过（targeted-*.trx）。包含原备份／容量／排除规则回归、独立.NET GCM空密码解密、v1—v5改后缀读取、错误密码及密文篡改、空密码导出重导入零联网、取消后原文件保全及成功原子替换。
- 不以修改后缀判断文件格式，仍验证认证头、大小、版本和载荷。旧非空密码和空白语义回归通过。

文件选择器／对话框的真实GUI、正式安装升级及ARM64实机待测；最终跨架构签名矩阵在.55。未操作正式挂载或真实远端，不重启Explorer、不补跑.33停止专项。不推送、不改动master。

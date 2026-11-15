# 0.0.21.0 验收记录

## 实现与边界

统一 WebDAV 菜单和显式 URL 表单，移除 HTTPS 预设、HTTP 勾选及表单厂商教程。空白／半输入允许作为草稿，提交时仍校验完整地址、账户／查询／片段限制及证书。新建和导入按 URL 生成内部 AllowHttp；已有地址未变保留旧值，不批量改名或修改根。HTTP 警告、HTTPS 证书、地址变更清草稿指纹、认证接管及取消隔离保持。

正式连接与证书捕获都使用系统推荐 SslProtocols.None，不调整系统策略。正式传输在完整握手后的 PlaintextStreamFilter 中借用 SslStream 读取目标协议，要求 HTTPS、已认证／加密及目标主机匹配，不包装或释放流，不新增请求。证书捕获、拒绝及代理外层不作成功证据。TLS1.3 实测为支持；TLS1.2 仅显示实际版本，1.3 为未知；HTTP 显示未使用 TLS。

能力和传输证据原子发布，后续元数据更新与局部清理保留传输快照；临时草稿测试不能覆盖已连接会话的实际 TLS。会话重建与草稿变化继续退役旧结果。模型仅保存协议枚举／时间，不保存地址或认证材料。公共 IPC、SQLite、注册表及 .hmxbk 不变。

## 实际验证

证据：`artifacts/validation/00210-work/`。

- `minimum-debug-build.log`：最终 x64 Debug 非打包 0 警告 0 错误。
- `debug-build-tests.log`／`debug-initial.trx`：首轮 73/73；追加 `debug-transport-final.log`／同名 trx 14/14，含新增活动连接与临时 TLS／重连证据保护。两轮部分用例重叠，不合并计数。
- `release-build-tests-package.log`／`release-final.trx`：扩大 Release 395/395，无失败、无跳过，包含 WebDAV 协议、证书、网络、能力、连接接纳和备份。
- 本机真实 SslStream 分别强制 TLS1.2／1.3，生产传输使用 None；验证实际协商、后续元数据不丢快照、只发 PROPFIND／OPTIONS、错误证书及取消无成功记录。真实认证代理外层流与目标主机不符时拒绝作为目标证据；这不是完整 HTTPS 代理 CONNECT 集成验收。
- 纯草稿覆盖新建／导入、空白／缺少协议、自定义端口、原 URL 不重写、未变更的旧 AllowHttp 保留；快照不可变、HTTP／新会话不继承 TLS、临时测试不覆盖活动连接、重连和输入代次退役。
- `webdav-ui-debug-final.json`／`webdav-ui-release-final.json`：双配置各 25 条（启动版本＋12组三语言×明暗×两密度普通表单＋12组导入表单），Failures 为空。最小 600 宽真实隔离 WinUI 验证三类地址、四类非法地址、明文警告／证书切换、HTTPS→HTTP 清指纹、TLS 三种展示、展开不启动探测、无旧教程、导入 URL／凭据／指纹保留和零配置／零任务。
- 双架构 Release 自包含签名 MSIX、验签及包检查通过；本目录保存两架构 SHA256／验签日志。`arm64-tests-build.log`：交叉编译 0 警告 0 错误，未执行 ARM64 测试。

## 过程失败

首轮最小构建因新增动态 key 未列入封闭资源映射而被验证器拒绝；改为明确 TransportKey 映射并纳入校验后通过，原日志保留 `minimum-debug-attempt1.log`。首轮 UI 探针只等待 40ms，在一次异步 TextChanged 处理前断言可见性，保留 `webdav-ui-debug-attempt1.json`；诊断重跑及改为最多 500ms 等待期望展示后，最终两配置各 25 条通过。没有因此绕过产品校验或修改异常处理。

## 待测试

实际鼠标／键盘完整流程、系统选择器、高对比度、跨屏 DPI、VS 现场密度中断、真实 HTTPS 代理 CONNECT／重协商与长期连接池、Easy Connect、实际 WebDAV 服务、Windows 11 21H2 和 ARM64 实机待测。隔离程序不代替生产及设备验收。

不安装生产包，不操作正式挂载或重启 Explorer；本地提交，不推送。

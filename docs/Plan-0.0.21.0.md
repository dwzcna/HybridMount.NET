# 0.0.21.0｜统一 WebDAV、自动识别 HTTPS、清理厂商提示

2026-11-15用户正式授权的替代方案；接在0.0.20.4后依序实施、验证并独立本地提交。不推送、不安装生产包、不操作正式挂载、不重启Explorer。源码及定向回归预计2—3工作日，设备人工另计。

新增挂载仅保留WebDAV。地址必须显式http／https，按URL识别，不增加HTTPS开关，移除允许明文HTTP复选框；不自动连接、修改端口或协议。HTTP提示明文风险，HTTPS显示现有指纹和证书操作。失败保留完整草稿，认证接管沿.20.1，导入预填沿.20.2，布局沿.20.3。

删除WebDAV表单内群晖WebDAV教程、WebDAV Server HTTPS套件说明、DSM5001入口提醒、5006惯例和nas.example.com:5006示例。统一示例https://dav.example.com/dav/；清理三语言及无引用键，不留空白。独立群晖NAS协议和必要反馈保留，用户端口地址不变。

删除独立HTTPS入口与httpsOnly分支，保留FileSourceKind.WebDav和内部AllowHttp兼容字段。新建／导入按URL生成HTTP=true、HTTPS=false；已有地址未变保留其原AllowHttp，不批量改写名称、ID、凭据和根。地址改变继续原保护，退役认证／证书／能力证据；HTTPS改HTTP清除草稿证书，取消不修改配置。其他协议、公共IPC、SQLite和.hmxbk格式保持。

TLS采用系统推荐SslProtocols.None，允许系统／服务器协商TLS1.3并保留TLS1.2，不改系统策略或降成HTTP。保留代理、SNI、指纹、超时及连接栈。通过握手后的PlaintextStreamFilter读取目标实际协议，不增加请求；代理外层TLS及获取指纹后主动中断握手不作为成功证据。内存能力增加RFC8446（TLS1.3）及可选协商版本／观察时间，绑定当前连接；实际1.3才验证，1.2显示实际版本但1.3未验证，HTTP显示未使用TLS。安全日志记录版本和阶段，不记录认证材料。

验证HTTP／HTTPS、非法地址、自定义端口、往返及旧配置；三语言提示删除与证书；本地TLS1.3／1.2、证书／指纹、代理、取消迟到；认证保留输入、窄窗键盘，双配置定向、双架构构建／签名包。生产机、Easy Connect、21H2及ARM64设备待测单列。证据存artifacts/validation/00210-work，更新README、Changlog和开发／验收记录后独立本地提交。

参考：https://www.rfc-editor.org/rfc/rfc8446.html 、https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslclientauthenticationoptions.enabledsslprotocols?view=net-10.0 、https://learn.microsoft.com/en-us/windows/win32/secauthn/protocols-in-tls-ssl--schannel-ssp- 。

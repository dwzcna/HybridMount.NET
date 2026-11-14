# 0.0.18.1 与 0.0.19.0 正式交接实施边界

用户通过生产故障分析任务正式交接，2026-11-15接收。保全0.0.18.0当前修改；依次完成0.0.18.0、0.0.18.1、0.0.18.2摘要、0.0.18.3权限、0.0.18.4元数据gzip、0.0.18.5同源301／308，然后实施0.0.19.0。各版独立本地提交，不推送、不安装生产包。0.0.18.1以下方案完整替代旧连接草稿。

## 生产证据与约束

生产机为C:\Users\szjsb01，经Sangfor Easy Connect访问NAS；RaiDrive相同地址和账号能列目录，浏览器HTTPS GET有404且叶证书SHA256与输入一致。生产只读对照：curl -vk快速TCP／TLS并观察到两次TLS重协商；不带-k快速SEC_E_CERT_EXPIRED。Framework HttpWebRequest（忽略证书、Proxy=null）和Core Invoke-WebRequest约22秒连接超时，前者目标为IPv4映射地址；系统／WinHTTP代理均直连。显式InterNetwork Socket约9ms成功，IPv6对照失败。

据此修补双栈／IPv4映射连接兼容，具体驱动丢包机制未抓包证实。另一受限VM的链检索阻塞不能代替本次证据；0.0.17.1指纹接受链未被证明有缺陷，故障发生在证书校验前。与NativeRuntime ParentPackageMismatch独立。不要查询开发机应用日志，也不要从开发机访问生产192.168.1.2代替生产验收；生产证据由用户操作提供。

## 0.0.18.1 地址族连接、探测及证书获取

- WebDAV实际传输改为SocketsHttpHandler.ConnectCallback；所有请求共用连接器。IPv4字面量用InterNetwork；mapped地址先MapToIPv4；真实IPv6用InterNetworkV6且DualMode=false。域名保留各族DNS顺序，IPv4优先、250ms后启动IPv6，最多同时两个候选，失败尝试后续，成功取消并释放其他Socket。
- DNS与所有候选共用15秒期限并服从更短调用者期限及取消；成功NetworkStream拥有Socket并交连接池。回调使用提供的DnsEndPoint，保留系统代理／CONNECT、原Host／SNI／名称校验；只返回TCP流，TLS由Handler执行，保持HTTP1.1。竞争只发生在HTTP发送前，不增加写重放。
- 保持系统验证成功或当前挂载指纹精确匹配，保留归一化／隔离／分类；并发证书诊断对应请求，连接复用不伪报新TLS。正式链／吊销策略不额外修改，保持系统TLS选择，重协商每次同规则。
- 只读连接测试先PROPFIND Depth0验证集合，再OPTIONS最多5秒；OPTIONS超时、405／501或方法单独权限限制不推翻有效目录读取，只标能力未知／受限。PROPFIND错误明确失败。WebDAV探测120秒总预算，取消工厂额外30秒截断，阶段与重试共用剩余预算，用户取消单列。服务器根地址合法，不强制共享目录。
- 结构化记录DNS、TCP、实际Socket地址族、TLS、方法／状态、阶段耗时及取消来源；TCP未完成明确尚未进入证书校验。诊断不包含凭据。
- 证书区在清除按钮左侧新增获取指纹。独立FetchCertificateAsync只验证HTTPS地址，不要求账号、密码、本地目录或合法旧指纹。复用连接器／代理，独立连接仅捕获叶证书公开信息，不发送DAV凭据，不依赖HTTP响应；专用获取关闭联机补链，正式TLS策略不变。15秒总期限，成功规范为64位大写SHA256并直接覆盖TextBox，显示主体／颁发者／有效期，不加二次确认。失败取消保留原输入；忙时阻止重复获取、测试和保存，关闭退出取消，草稿变化丢弃迟到结果。获取不代表DAV测试成功。三语言语义键、窄窗换行及键盘顺序同步。
- 回归实际地址族、mapped／IPv6／DNS失败切换、连接／DNS取消与期限、竞争资源回收／连接池；正常、过期、名称不符、自签、正确错误指纹、替换、重协商和并发隔离；OPTIONS阻塞／受限、PROPFIND错误／正文停滞／零写；无凭据获取、成功覆盖、失败保留、根404、关闭迟到。隔离结果不代替Easy Connect生产验证。
- 生产验收由用户在Easy Connect下完成获取填框→测试→列目录→读取，确认实际纯IPv4路径。Debug／Release定向、双架构构建和签名包检查，真实生产及ARM64设备未测单列。

## 0.0.19.0 标准资源与UI命名

- 已确定C#直接使用System.Resources.ResourceManager.GetString语义字符串键，不生成强类型访问器、不新建Text包装。Messages.resx以zh-CN为中性资源嵌入，Messages.en-US.resx与Messages.zh-HK.resx为卫星，声明并验证标准回退。带参数用string.Format显式CurrentCulture；协议／持久化保持InvariantCulture。
- XAML使用.resw／PRI、x:Uid和Windows App SDK MRT ResourceManager／ResourceContext，显式语言上下文，保留打包／非打包PRI定位。按实际消费者分配resx／resw，删除无用副本及UiCatalog第三套源。原生RC／MUI保留，语义键和原生ID明确映射，不从中文SHA256计算。
- 删除UiText.Text、哈希键及生成依赖，删除Legacy／Describe原文或正则反向翻译。ResourceMessage仅为语义键＋参数或原始文本载体；第三方异常不猜译。
- UILanguageService管理zh-CN／en-US／zh-HK、系统匹配与变化通知；CurrentUICulture／有效UICulture选资源，启动／切换同步UI和默认UICulture，后台显式取语言快照。CurrentCulture及默认区域Culture保持系统，不因界面语言改变日期／数字／货币。检查根Language、DatePicker、NumberBox和日志筛选。
- x:Uid现有控件按明确标识刷新，保留最小通知和动态对话框更新；保持未保存草稿、选择、滚动、关闭InfoBar和后台任务，不重建应用或重发网络，不扫描现有文本正则替换。
- 所有项目自有UI含义文件／目录／类型／接口／命名空间／成员／资源／脚本标识用UI或UserInterface。明确重命名UiLanguageService／Selection→UILanguageService／Selection，NativeUiLanguageBridge→NativeUILanguageBridge，IUiDispatcher／UiDispatcher→IUIDispatcher／UIDispatcher，UiTerms→UITerms，Helpers/UI的Ui→UserInterface，自有WinUiFrame→WinUIFrame。废弃类型直接删，第三方／框架如x:Uid保留拼写。Windows仅大小写路径经临时名改名并核对Git。
- 注册表语言值、共享内存和诊断字段同版改UI，继续用户／会话／包族／模式隔离。按全新安装，不迁移旧任务、哈希、UiLanguage值或旧共享内存，无别名或兼容接管，不操作机器已有数据。
- 重写ValidateResourcesTask：语义键、三语言完整性、格式占位符、原生ID映射；缺失／重复／无效／不匹配构建失败，动态键改明确静态映射，运行时标准回退。审计无UiText.Text／哈希／UiCatalog活动依赖和自有Ui命名。
- 非WinUI测试实际嵌入resx与卫星并读取三语言／回退；打包及非打包实际PRI、C#卫星和RC／MUI一致。交叉UI语言和系统区域格式，跟随系统、保存重启、运行切换、后台消息／对话框／草稿／选择／滚动／关闭提示、DatePicker／NumberBox／日志、长文／长路径／窄窗／缺失反馈。Debug／Release、双架构及签名包检查，设备生产未测如实记录。

## 核心参考

- https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0 （示例无地址族Socket不能照抄）
- https://learn.microsoft.com/en-us/dotnet/core/extensions/create-resource-files
- https://learn.microsoft.com/en-us/dotnet/core/extensions/work-with-resx-files-programmatically
- https://learn.microsoft.com/en-us/dotnet/api/system.globalization.cultureinfo.currentuiculture?view=net-10.0
- https://learn.microsoft.com/en-us/dotnet/api/system.globalization.cultureinfo.currentculture?view=net-10.0
- https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/localize-strings
- https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/mrtcore-overview

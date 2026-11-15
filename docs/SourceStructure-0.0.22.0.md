# 0.0.22.0 源码结构

本说明描述当前四工程布局；历史职责说明保留为历史资料，不再用旧目录指导新代码。未新增工程、DI框架、Abstractions或命名空间转发层。

| 目录 | 当前职责和真实边界 |
|---|---|
| Views/Pages、Windows、Dialogs | 页面、窗口及模态表单，XAML与代码同目录 |
| Controls | 复用卡片、字段、能力列表和详情控件 |
| ViewModels | 页面、任务行、挂载行、备份行及排除规则草稿，统一ViewModel后缀 |
| UI | 本地化、布局、绑定、格式化、原生Frame与WinUI托盘适配 |
| Models | 业务数据、纯规则、恢复证据、运行时DTO及快照 |
| Commons | 按主题组织的持久化、协议、调度、设置、日志和Windows接入契约 |
| Services | 挂载、同步、任务、调度及设置编排，保留原锁和工作所有者 |
| Infrastructure | 协议SDK／HTTP、SQLite、文件、凭据、恢复、CFAPI及原生IPC具体操作 |
| Composition | 手动装配；展示器在模块初始化时安装，不创建控件或访问系统 |
| Helpers | 仅基础库的通用路径、流及共享异步工作工具 |
| NativeShared | 两原生工程编译同一份语言、身份、IPC基础和缩略图源码；第三方头及版权随迁 |

Commons可引用Models；两者不引用Services、Infrastructure、UI或WinUI。Infrastructure不反向调用具体Services、Composition或UI。Services不依赖WinUI和视图；仓库、日志、偏好、协议工厂与调度通过窄契约装配。本版未另建服务定位器或并行任务消费者。

数据库仍由MountRepository持有原来的锁、连接和事务；IMountRepository按原职责拆为partial契约，转发不移动提交位置。协议SQL模块是基础设施内部协作接口，事务句柄不进入Commons。认证交接使用IAuthenticationSession和原AsyncLocal作用域，null作用域也会隔离父级票据。CloudFiles返回模型或JSON回执，句柄仍由原生端持有。

模型的旧派生字符串及已有JSON字段保留，getter仅调用Commons展示契约；实际资源和格式化归UI/Presentation。展示view按不可变快照弱缓存，语言在读取时取得。ResourceMessage只存Key、参数和Raw；模板与参数一起交给UI渲染，后台不直接访问ResourceManager。日志通知使用纯PageMessageSeverity，Dispatcher在App装配时注入，不由后台日志服务捕获UI线程。

注册恢复快照仅保留BCL的RegistryHive／RegistryValueKind枚举数字编码，不调用注册表；不复制一套枚举或改变已有恢复JSON。集合DTO允许同文件；所有partial类型集中同目录。测试MockFileSource是唯一目录／命名空间例外，用生产模拟源命名空间替换真实适配器。

Stock按Runtime、CloudFiles、FileSystem、Previews、Thumbnails、Diagnostics和Resources分类；Ext按COM、Menus、Previews、Thumbnails、IPC和Resources分类。工程名、输出文件、COM导出、包身份、IPC v2、SQLite schema8和.hmxbk均保持。

`Directory.Build.targets`为四工程导入同一个只读结构检查。它核对命名空间、禁止依赖、partial集中、真实Compile集合、测试生产链接、XAML类型和原生共享登记；设计时不执行。独立检查：

```powershell
dotnet msbuild build/HybridMount.StructureValidation.proj -v:minimal
```

编译还执行原资源门禁，继续验证三语言RESX／RESW与原生MUI。生成目录、第三方代码及上述纯枚举／测试桩例外明确处理，不通过增加宽泛忽略绕过规则。

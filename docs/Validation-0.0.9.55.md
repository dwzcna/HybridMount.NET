# 0.0.9.55 验收记录

基线625a19f（.54），继续codex/native-backend／当时的预览身份；按用户最后顺序在PR批次、.53备份、.54队列后实施目录整理。所有产品文本改动先写版本补丁，git apply --check后应用；没有直接在目标文件上生成未检查改动。证据位于artifacts/validation/00955-layout/，未纳入产品或提交。

## 实现与兼容边界

- 主目录／工程改为HybridMount.NET/HybridMount.NET.csproj，输出仍为HybridMount.NET.exe；原生目录／工程／输出改为HybridMount.Stock/HybridMount.Stock.vcxproj、HybridMount.Stock.exe。Ext、Tests保留，共四工程，Debug／Release及x64／ARM64。
- Services保存应用生命周期服务及集中后的MountManager partial；Models按协议、持久化、会话、事务、运行时及Windows接入归类。Commons替代Common，Controls接收普通控件和对话框，Helpers接收流、路径与通用工具；App和Program仍是根入口。子命名空间与目录一致，同一partial类型集中；C#根命名空间保持HybridMount。
- MainWindowViewModel管理导航、通知和同步问题快照，窗口负责视觉与平台操作；MountBackupRow迁到ViewModels并去掉WinUI依赖。共享样式由App.xaml引用Resources/SharedStyles.xaml。
- 同步解决方案、测试源码链接、构建／打包入口、COM可执行路径、进程身份核验、C++包含、XAML及资源URI。更新README为新入口。包身份、CLSID、同步根ID、管道、配置／备份、SQLite及数据目录不因改名迁移；旧挂载无需迁移。六个关键文件的路径、协议、备份及SQL字符串审计见compatibility-constants.json，允许的映像名变化为Native.exe到Stock.exe。
- TasksPage ↔ TasksViewModel ↔ TasksQueueService及.54串行／优先级调度保留；Shell缓存直读／完整本地解码仍独立于网络调度，不增加取图IPC。

## 实际自动验证

| 项目 | 结果及证据 |
| --- | --- |
| 最小x64 Debug非打包 | 通过，minimal-build2.log |
| 改名／原生运行时 | 70/70，targeted-Debug.trx |
| 布局／页面／主窗口状态 | 70/70，layout-contracts-Debug.trx |
| x64 Debug最终广泛回归 | 1495/1495、0跳过，final-wide-Debug.trx |
| x64 Release最终广泛回归 | 1495/1495、0跳过，final-wide-Release.trx |
| 非打包构建 | x64 Debug／Release、ARM64 Debug通过，matrix-*-Unpackaged.log |
| Release MSIX | x64／ARM64自包含构建、签名及内容检查通过，matrix-*-Release-Package.log |
| 测试工程ARM64 Release | 交叉编译通过、0警告／错误，tests-ARM64-Release.log；未在ARM64运行 |
| 源码登记 | Compile260／Page12／App1，无遗漏、重复或缺失文件；namespace与partial目录一致，source-registration.json |
| 资源登记 | 双架构13项窗口／页面／控件／共享样式的PRI EmbeddedData URI通过，package-resources-*.log |

广泛回归按用户要求排除FailedMountTests、OrphanedMountRecoveryTests、ClearRemovalTests三个.33停止专项类；沿用限制并行度配置，同输出目录及真实CFAPI／Explorer验证串行。包检查核对实际Stock.exe、两架构PE、无CLR头、无测试专用原生操作及正确COM入口。广泛回归包含原生宿主模块检查、缓存命中／未命中、完整本地回退、关闭主服务、远端范围预算、取消和任务队列交错；源请求及内容／Pin／InSync保护以各实际断言为准，不以编译替代运行。

## 隔离真实MSIX与Shell

使用当前.55测试程序集和生产HybridMount.Stock.exe，独立临时包身份／CLSID／新测试根，只访问回环WebDAV与本轮文件。测试驱动为C#，CFAPI／COM／媒体解码实际角色为C++；没有用测试驱动冒充无CLR宿主证明。未重启Explorer、未操作D:\wdavtest\坚果云相册等正式挂载。

1. desktop3Layout-5ce564e3：首轮No generated image，保留error.txt和timeline.json。可见项是item-0298.txt及item-0299.txt，源GET和生成均为0；夹具没有提供可见媒体，不能据此将产品判断为解码失败，也不能将本轮记为缩略图通过。精确卸载及包外注销核验随后完成。
2. desktop3Layout-ee759a15：仅调整忽略目录的夹具，将600项均设为BMP、限制统计范围、把自建测试窗口放到可见区域。baseline／enabled／hot／disabled共124次有效采样，逐阶段行名、坐标与焦点无变化。启用后item-0298／0299生成，GET／通知为2／2；主动切换视口后item-0593／0594继续生成，GET／通知为4／4且保持稳定。关闭阶段采样均为5／5、生成集合为空，未继续增长；阶段前夹具会主动切换视口，不把这一动作算成产品跳动。enabled-image.json通过真实Shell图像工厂返回HRESULT 0、64×32、2048识别像素。详见timeline-summary.json。
3. desktop3BindingFinal-2020d9f4：独立包外进程读取冷文件及本地完整文件，系统图像工厂自动返回缓存识别像素，两者HRESULT 0、64×32。缓存像素与实际正文不同，显式COM对照单独记录。NativeTrace证明GetThumbnail和CreateMenuHandler调用；原生固定／释放菜单、System.StorageProviderUIStatus及ProviderState 1／3保留。实际AUMID为该临时包FamilyName!App，原生ping一致，源请求8→8。文件NTFS／占位身份、大小、写入时间、Pin、InSync、水合及修改范围保持；本地完整文件的USN／ChangeTime变化原样保留，不宣称所有元数据逐字不变。详见result.json、outside.json、binding-summary.json。

三份临时包均另建零字节根，正常断开保留注册后卸载；独立非打包进程确认原生根、SyncRootManager及默认视图Namespace不存在（uninstall-proof.json各Passed=true）。随后对主测试根和卸载根的全部视图作更严格包外收尾，见下节。自动化Shell接口对照不替代侧栏／磁盘路径及鼠标的人工目视。

本轮全部124次光标采样为arrow，但指针在测试内容窗口外，**不能将忙碌指针生产问题记为人工验收通过**。没有强制设置鼠标、恢复视口或改变产品观察器来使探针通过。

## 精确清理与包外复查

- 先核对当前用户所有权、测试路径、原注册快照、根已失效及无活跃引用，再清理752项非打包测试CLSID；备份及删除证明为artifacts/validation/00955-test-registry-*.json，Remaining为空。
- 90个测试缓存库仅在当前SID、唯一挂载、GUID文件名、临时根已不存在、无重解析路径和哈希备份通过后删除；对应WAL／SHM一并核对。00955-test-cache-*.json记录Remaining为空，三个非目标库保持。
- 包外完整审计发现临时包尚有部分Shell注册残项：两个滚动夹具主根的Namespace／CLSID以及卸载后的32位Shell类等。系统自动注销原生同步根的通过不等于全部Shell视图无残留。按三个临时包的实际FullName、根ID／路径、图标路径及所有者逐项备份16份视图记录，实际删除12项（共享视图映射使其他快照同时消失）；00955-packaged-registry-*.json记录Remaining为空。不扩大到旧测试或正式挂载。
- 累计768份注册快照、764次实际删除。final-package-audit.py及packaged-final-audit.json复查三包六份根证明、COM类、Namespace／隐藏值及缓存无本轮残留；packages-removed.json确认三包均未安装。唯一保留的测试根目录为空，原生查询0x80070186确认不是同步根，属于本轮artifacts证据目录。
- 用户已清理的三个Thumbnail-00945旧入口未处理；本轮绑定探针复用历史测试名称但使用独立新GUID，清理仅按本轮证明执行。没有清理未归属历史项或非目标缓存。

## 阶段失败及修正

初始命名空间派生产生HybridMount..、系统Windows命名空间遮蔽、App.xaml类名及旧路径结构断言在编译／测试中暴露，均以受检补丁修正。关键字符串审计辅助器首版误读C# verbatim string，修正辅助器后通过，没有修改兼容常量；源码登记辅助器对GlobalUsings.cs先误要求namespace，确认其只有全局导入后单独列例外。首个真实包夹具缺少可见媒体，按上节保留失败并调整夹具复测。包XAML实际为PRI EmbeddedData，新增资源核验依据真实URI，没有把不存在的独立.xbf ZIP项当成产品遗漏。最终通过不覆盖这些原证据。

## 产物与待测

签名证书指纹3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0，两包签名验证0警告／错误：

- artifacts/packages/x64/HybridMount.NET_0.0.9.55_x64_Test/HybridMount.NET_0.0.9.55_x64.msix
- artifacts/packages/ARM64/HybridMount.NET_0.0.9.55_arm64_Test/HybridMount.NET_0.0.9.55_arm64.msix

仍待人工WinUI导航／对话框／任务页、真实相册与鼠标指针、Explorer侧栏及磁盘路径交互、正式MSIX安装升级、系统编解码器差异和ARM64实机。没有把隔离临时包安装等同正式安装升级验收。系列PR仅完成本地移植整合，GitHub PR没有执行merge／关闭；master保持ba424e1。本版一个“0.0.9.55｜规整工程名称与代码职责目录”本地提交，不推送；不补跑.33停止专项。

# HybridMount 0.0.9.1 验收记录

2026-11-04，Windows x64，.NET10.0.401／VS2026／Windows SDK26100。原开发与自动验证预算60–100分钟，追加完整协议表单、SQS内联和多轮截图修订后增加30–60分钟，GUI／实机等待另计。三个工程、现有SDK、IPC v2、SQLite v6、定义版本10和备份v4保持。产品、程序集与MSIX均为0.0.9.1。

## 已完成代码

- 挂载页移除DetailsPanel。每项有背景、圆角及边框；第三行固定32 DIP，选择前后同高。名称、协议／目录、已用／总空间与百分比按用户布局排列；容量两位小数、比例一位，未知不当零。
- 十个图标操作按连接／设置、手动扫描／检测、两个快捷开关、固定／缓存／容量和移除分组，以细竖线分隔。全部放下时隐藏DropDownButton，溢出时只收起后续操作；按钮、间距与分隔线都计入宽度，菜单分隔符不留空组。菜单固定打开时的挂载ID，回收时关闭。
- 连接、缩略图与远端自动检测使用ToggleButton，Tooltip及可访问名称表示实际动作。连接和保存期间不乐观切换；失败保持实际值。快捷检测只保存开关，不提交未保存的间隔草稿。远端检测使用16 DIP字体图标以避免裁剪。
- 右侧打开目录使用ED25；原日志入口改为E9D9完整诊断，ContentDialog显示该挂载的Detail，可换行、滚动和选择文字；不再跳全局日志页。
- Views/Mounting/WebDAVConfigurationContentDialog.xaml、S3ConfigurationContentDialog.xaml各自包含完整表单。连接、凭据、容量、缩略图及远端检测在同一窗口；不保留公共表单XAML或字段适配接口。Common只保留必要的IMountConfigurationDialog生命周期边界。
- SQS使用S3窗口内默认收起的Expander，包含模式、URL、区域、凭据、只读测试及保存。独立SqsSettingsDialog移除；探测可取消，普通关闭和退出发停止信号，在途请求由应用所有者等待，未提前释放令牌。密码控件关闭时清空；不读取默认凭据链内容。
- 页面只适配视图，唯一MountsViewModel负责按ID保存及忙碌；保留验证、DPAPI、根编辑限制、连接失败配置保留、状态回滚和重复提交保护。刷新复用行、保留选择／草稿／滚动，离页不停止后台挂载。

## 实际验证

证据位于artifacts/validation，各批有重叠，不相加。

| 范围 | 实际结果与证据 |
| --- | --- |
| WinUI最小编译 | 逐批非打包编译通过；最终输入由0091-delivery-Debug.log、0091-delivery-Release.log验证。两个完整协议对话框已真实编译，非仅测试替身 |
| 页面相关针对性 | 45/45、零失败／跳过：0091-final-models.log、0091-final-models.trx；包括新增快捷状态与连接校验 |
| SQS内联针对性 | 39/39、零失败／跳过：0091-inline-sqs-tests.log、0091-inline-sqs.trx，含页面、行状态和SQS服务回归 |
| 完整x64 | 最终Debug／Release各494/494、零失败／跳过：0091-delivery-Debug.log、0091-delivery-Release.log、0091-delivery-contracts-x64-Debug.trx、0091-delivery-contracts-x64-Release.trx |
| 双架构签名 | x64／ARM64的0.0.9.1 MSIX签名与包检查通过，签名验证零警告／错误：0091-delivery-package-x64.log、0091-delivery-package-ARM64.log |
| ARM64交叉编译 | 测试工程成功、零警告／错误：0091-delivery-tests-arm64.log；没有ARM64硬件运行结果 |
| 结构与资源 | 165个源Compile无重复，两个完整协议Page及MountCard各登记一次；两架构PRI含对应XBF，旧NetworkMountDialog、公共MountConfigurationForm资源无残留：0091-final-compile-items.json、0091-final-test-compile-items.json、0091-final-structure-audit.json、0091-final-resources-x64.xml、0091-final-resources-ARM64.xml |

隔离模型验证容量未知／不限／超额及固定小数，连接中取消、取消防重入、断开和重试，稳定行引用、选择及按ID保存不串挂载，快捷开关不提交间隔草稿、保存失败真实状态保持，连接校验失败后解除忙碌并保留定义。完整回归执行现有协议、上传、远端更新、实际NTFS／CFAPI、读取、预览、取消／断开／移除、SQS、备份和页面模型用例。

构建使用本机固定版本NuGet缓存源，关闭该次在线审计，未更改仓库源、证书或安全设置。所有产品非二进制变更先编写补丁、git apply --check再应用；构建产物与辅助工具保持忽略。

## 修正与验证边界

- 中间编译发现ToggleButton命名空间、StackPanel不支持IsEnabled及完整展开后的字段笔误；经补丁修正后最终编译通过。失败日志0091-card-controls-build.log、0091-configuration-build.log、0091-complete-dialogs-build.log保留，不能把失败尝试记为通过。
- 首次资源辅助审计假设XBF是ZIP独立文件而失败；实际XBF嵌入PRI，改为MakePRI导出两架构资源映射验证，未修改产品资源格式。
- 用户截图作为需求及缺陷反馈，不代表代理完成GUI验收。代理没有启动／重启正式进程、停止用户Debug Packaged实例、改正式挂载配置或访问真实远端；NAS10.0.0.20:5007测试保持取消。
- 实际宽／窄窗及临界宽度、高DPI／高对比度、Tooltip／键盘、菜单虚拟化、完整配置输入、SQS折叠／测试取消／保存与退出、Explorer、安装升级、非打包启动和ARM64实机继续待测。自动测试和受控CFAPI不能代替这些结果。
- SHELL-01、TEST-RUNNER-01、历史TLS／调试器现场、真实RFC6578／AWS SQS及其他审查待办继续单列，本轮全量通过不代表相关根因修复。

## 产物与提交

- `artifacts/packages/x64/HybridMount_0.0.9.1_x64_Test/HybridMount_0.0.9.1_x64.msix`，SHA256 `a2148c270e2b6f23eb8c168a9624c065fbe15e8c102c66e28e6363fc58f4fe99`。
- `artifacts/packages/ARM64/HybridMount_0.0.9.1_arm64_Test/HybridMount_0.0.9.1_arm64.msix`，SHA256 `198fd66f22b87a0797daaff70d1eb48f0356e2e025f00a2dde4e935f893fedf1`。

签名证书3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0，产物不纳入源码。

本轮初始暂存仅包含MountsPage.xaml格式变更；授权提交前原blob仍为23b127d7e38c2c2d1501bc8bbe6e648050390d35，基线为ba5a782。按用户最新“0.0.9.1完成后提交，再开始0.0.9.2”授权，将本轮相关修改独立本地提交，不推送；0.0.9.2修改不混入本次提交。

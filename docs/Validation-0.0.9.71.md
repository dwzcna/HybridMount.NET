# 0.0.9.71 验收记录

## 范围与源码状态

版本统一为0.0.9.71。本版整合语义MRT属性资源、任务与日志页头、单日日志筛选、标准Win32 MUI，以及.70提交之后既有的构建／日志／无包初始化／界面／当前运行模式还原维护。仍使用当时的预览身份部署身份，正式身份和原生工程集中重构留到.72；公共IPC v2、配置和SQLite格式不变。

所有文本修改先写0.0.9.71_*.patch，经git apply --check、git apply成功后删除工作补丁；副本保存在artifacts/validation/00971-work/。早期“加载更早”追加的静态证据仍保存在artifacts/validation/00971-log-toolbar/，该阶段尚为.70，不能替代整版验证。

## 已完成的实现

- 主程序三语言源为HybridMount/Resources/zh-CN、en-US、zh-HK/Strings.resw；原HybridMount/Strings目录删除。1038键×3语言，静态x:Uid和Ui.ResourceId使用/Strings/语义路径，程序化读取实际PRI并兼容历史动态消息。清单名称、说明、启动及COM显示文本使用ms-resource:///Strings/...，生成任务保持同一规则。
- 任务全部／同步问题／冲突，日志全部／信息／警告／错误。稳定StackPanel图标和TextBlock内容保留实例，只切换文字可见性。右侧操作先前三项、筛选全文、其余操作；少于三项先展示全部。扩展8DIP余量、收缩即时适配，原按钮移动到原生溢出，保留Click、AccessKey及禁用状态。
- 日志搜索和可空DatePicker位于上行，清除日期仅有选择时出现，并触发窄窗换行；下行筛选及操作，加载更早为操作第一项。查询、分页、实时匹配和导出固定同一条件；本地单日分别换算两端UTC零点，覆盖23／25小时日。
- 原生资源位于HybridMount.Ext/Resources/<语言>/NativeStrings.rc。RC／LINK／MUIRCT生成HybridMount.Ext.resources.dll及三套同名.mui，LN／卫星共享校验和、最终回退zh-CN；专用线程加载，恢复线程偏好并释放模块，Explorer热路径仅用内存。
- 整合.70未提交维护，保留其详细历史：MSBuild任务替代VS自动PowerShell；GUID字段N／D读取、D写出及导入保护；原生包身份判断、覆盖与摘要分别失败处理；Subtle样式、紧凑启动卡片、独立可关闭InfoBar；仅当前模式还原、配置锁内确认、停写、目录身份／链接保护及清理。

## 实际执行的验证

全部证据根为artifacts/validation/00971-work/。

| 验证 | 实际结果与证据 |
| --- | --- |
| x64 Debug最小及非打包构建 | 通过；final-x64-Debug.log、final-native-UI-x64-Debug.log |
| x64 Release非打包最终交付 | 通过；delivery-final-x64-Release.log |
| 相关回归 | Debug 148/148、Release 148/148，0失败／0跳过；final-Debug.trx、final-Release.trx |
| 标准MUI真实加载 | 5/5；native-mui-Debug.trx。三语言、LN／MUI元数据、共享校验和及缺失／不支持语言回退 |
| 资源校验保护 | 7项通过：有效输入接受；大小写重复、前缀冲突、语言缺项、UID不一致、错误属性类型、缺失清单引用均拒绝；final-resource-guard-results.json |
| 实际PRI | Strings资源映射／属性路径核对通过；strings-pri.xml、unpackaged-pri.xml及对应日志 |
| 真实WinUI隔离运行 | 五页×三语言×四宽度、页面实例／草稿／选择、独立关闭消息、任务筛选及实际PRI通过；smoke-final-isolated-result.txt |
| 页头门槛与日志 | 英文工具栏484DIP为2项紧凑、605为3项紧凑、835为3项全文、959为4项全文；单日清除、窄窗重排、135行分页／导出与禁用状态通过 |
| 最终签名MSIX | x64／ARM64均通过签名和实际包检查；package-final-x64.log、package-final-ARM64.log及signature-*.log |
| ARM64测试交叉编译 | 通过、0警告／错误；tests-ARM64-Release-compile.log。未在ARM64运行测试 |

相关回归覆盖256条原生形状N／D及混合GUID导入／去重、提交后重放、非法内容和删除前变更保护；无包不调用包API、摘要仍发布及身份错误诊断；还原隔离路径范围、目录身份、链接、只读及共享占用；页头策略、日志日界／筛选与既有任务模型。

148项回归后仅追加原生语言模块RAII释放和日期控件SizeChanged重排；最终双架构包构建及隔离GUI复验覆盖这些收尾修改，没有将旧回归次数冒称新增重复运行。

最终包：

- artifacts/packages/x64/HybridMount_0.0.9.71_x64_Test/HybridMount_0.0.9.71_x64.msix，SHA-256 D820EFA7AAD02668B48CA861A75EEE4539270E1B49607CEC53389EF04AA6B6A6。
- artifacts/packages/ARM64/HybridMount_0.0.9.71_arm64_Test/HybridMount_0.0.9.71_arm64.msix，SHA-256 7653ED1A15687120E92C182C87DE25AF190E704E3BA564F066304D9E348F2576。
- 当前用户匹配Publisher证书签名，指纹3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0；final-packages.json保存最终哈希，未安装这些包。

## 阶段失败、修正与复验

- 动态置空SegmentedItem.Content后字符串模板未恢复，导致展开／紧凑测宽均为123；改为稳定组合内容并在测量前UpdateLayout，最终GUI门槛通过。
- MUIRCT卫星为SDK生成的I386资源格式，与目标代码架构无关；原包检查误判失败。现只对LN要求目标架构，卫星允许I386；二者必须无代码／无入口／无CLR，并用GetFileMUIInfo（dwVersion=1）校验type、checksum和最终回退。修正后双架构最终包通过。
- 还原共享占用测试原期待IOException，实际句柄保护返回Win32Exception；改为仅接受32／33共享占用码且验证原文件保留，最终相关回归通过。
- GUI重复运行固定数据库累计了多轮135条标记，导致分页断言失败；改每轮独享随机数据库，未改产品分页逻辑。最终隔离重跑退出0，结果完整保留。
- 中间构建／GUI日志和补丁副本保留，不删除失败证据。.70宿主证据阶段的256条哈希快照、之后源目录已为空但原因未核验的说明继续保留，不对现场数据变化作新归因。

## 待测试与边界

人工键盘／AccessKey／读屏、完整浅深色／高对比度／文字缩放、Windows语言真实更换及安装名称／已有状态列系统缓存、真实有包启动／正式升级、当前模式真实还原完整流程、DSM设备和ARM64实机待测。还原仅测试隔离路径保护，未执行产品真实清空。MUI官方流程已源码和真实资源加载验证，未把官方文档当测试证据。

未推送、未改master、未操作正式挂载、未重启Explorer、不补跑.33停止专项。运行中的正式.63宿主保持。后续.72按用户明确授权另做本地分支调整，本版未提前实施。

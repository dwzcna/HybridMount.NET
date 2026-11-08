# HybridMount 0.0.9.35 验收记录

日期：2026-11-08；Windows x64，.NET SDK 10.0.401、VS 2026、Windows SDK 26100。代码与本版自动验证完成，GUI／Explorer、最终包部署及ARM64实机保持待测。原开发及自动验证预算6—10小时，外部等待另计；没有把计划、历史结果或API可查询当作桌面验收。

## 实现与兼容

- 两个完整协议配置窗口增加文件名规则ListView：启用、包含／前缀／后缀、添加／删除／补默认项与独立保存。忽略大小写、任一命中即排除；目录名称不传播命中，拒绝空文本、路径及重复项。仅普通新建挂载明确保存四条默认项，旧定义与旧备份缺失规则为空；首次连接前可改，草稿及防重入归MountsViewModel，保存失败保留旧定义与草稿。
- 排除只拦上传，远端列表、检测及按需读取保持，不传播删除。未发送任务用Excluded保存原阶段；扫描、快照、辅助准备、最终发送、重试及副本均复核。在途及未知结果先完成原核对；解除后只恢复原可调度工作，不授权失败／冲突／主动取消重发。原生标记失败独立诊断，不伪造上传结果。
- 属性句柄调用SetPinIntent／CF_PIN_STATE_EXCLUDED=3，无递归。验证当前账户根、文件ID及占位归属；原子JSON先保存原Pin及阶段再修改。普通文件保持非占位，通过属性固定意图与Shell状态确认；不转换身份、不读正文、不强改InSync。应用内固定／释放跳过并说明排除；系统PINNED意图由规则重新核验，无额外GET或PUT。
- 标准StorageProviderItemProperties编号2显示灰色减号“已排除（不上传）”；上传问题编号1独立保留。共同适配器检查提供程序及完整向量，真正移除对应编号而非写空字符串。已完成标记只用共享属性读，只有改动才请求短暂保护；系统写入开始后等待完成再释放句柄。退出／取消不遗留后台写入；已记录的纯元数据及close USN不推进内容代次、不重新生成已清除任务历史。
- 重启可补Applied旧记录缺失的图标或继续清除；原先就EXCLUDED以及用户后来修改的固定意图保持。旧路径缺失不再直接丢日志：同卷NTFS FileId只读核对确认不存在后才清理，改名文件仍可恢复。无明确删除证据的错误（包括本机已回收ID返回87）保留日志，不猜删除或操作未知路径。此策略可能保留已删除文件的内部身份记录。
- 规则为不可变值列表，JSON往返保留记录相等性。注册表配置版本11新增可选规则、上传JSON新增可选恢复阶段；SQLite不变。备份v5读取v1—v4，旧格式编码仍使用原字段形状；密码加密、DPAPI、三个工程、SDK、文件源接口及IPC v2保持。
- 容量保存／抓取同排且长文可换行；更新自动开关、抓取按钮与一位小数可用／总量及来源；未知—、不限明确显示。抓取更新快照，不覆盖手动上限。WebDAV标题含RFC6578、移除重复模式说明，保留真实降级、最近成功和错误；S3不加RFC6578。挂载卡片第三行高度保持，自有缩略图／菜单扩展继续暂停。

所有目标文本先生成patch/0.0.9.35_*.patch，经git apply --check后应用并删除；图标由SyncExcluded.svg经build/Update-Assets.ps1 -StatusIconsOnly导出。辅助脚本、产物和日志留忽略目录。开工时工作区及暂存为空；本轮仅本地提交，不推送。

## 正式现场只读证据

用户截图中的IMG_4380.bak原生Pin=3、InSync=0，System.StorageProviderState=9；System.StorageProviderUIStatus没有额外状态图标。OneDrive的desktop.ini同为状态9，但带有自定义状态编号、文字和图标资源。证明缺少图标不能等同于规则或原生EXCLUDED未生效；补充标准存储属性，不修改只读System.StorageProviderState，不重启Explorer或重建正式根。

工具只读取D:\myweb及OneDrive指定文件的属性／Shell元数据，未读取正文、未写正式文件、未连接正式WebDAV／S3或已取消NAS。test.bak随后已不存在，只记录不存在。证据：artifacts/validation/00935-user-exclusion-readonly-metadata.log。

现场确认用户已安装运行dwzcn.HybridMount.NET_0.0.9.35_x64__e43t74r3a3xm8中间包，PID32300；随后子代理只读核对为PID21904，包内两个PNG均存在。工具未主动停止、卸载、替换它或重启Explorer；这不表示Explorer进程从未变化。最终包仍按约定使用0.0.9.35，工具未执行其部署。用户追加截图显示状态图标为普通文档而非灰色减号：目视失败作为已知问题交0.0.9.36，不能记成通过。

## 隔离原生与协议验证

协议均为本地回环DAV／S3夹具、独立NTFS／CFAPI根及运行索引，不作为正式服务兼容性结果。

- 默认四条、大小写、目录边界、草稿、保存失败、旧配置及v1—v5备份；排队、准备、发送边界、在途、未知结果、恢复、改名、S3及已清历史保护均有回归。
- 普通文件、仅占位、已固定及已释放文件正确设置EXCLUDED并读回完整图标元数据；内容、FileId、占位身份、InSync及缓存形态保持。设置本身不增加源请求，按需读取可用；用户PINNED意图被排除规则重新覆盖且不下载。
- 同步问题和排除提示相互保全；清除任一提示不破坏另一项，清除后向量长度一致。恢复Applied但缺图标的日志、原先EXCLUDED、元数据重复扫描不写USN、改名后清理图标均通过。实际修改期间存在短暂共享保护，读取验证在状态事务完成后执行。
- 测试夹具执行精确根收尾；一次注册失败留下的空测试根仅剩Shell入口，核对精确ID、路径、引用和原生0x80070186后备份清理，删除文件0。最终独立非打包注册核验测试引用为0，见00935-test-leftover-cleanup.json及00935-final-root-audit.json；不操作正式MySvr或旧版遗留清理任务，不补跑用户停止的0.0.9.33专项。

## 自动验证与原始失败

证据均在artifacts/validation/。未覆盖原始日志，完整目录汇总为00935-regression-catalog.json。

| 项目 | 实际结果 | 证据 |
| --- | --- | --- |
| 最小x64／双配置XAML | 通过 | 00935-batch1-build.log、00935-batch3-build.log |
| 规则／队列／备份 | 89/89 | 00935-batch2-final.trx |
| 编辑器与扩展原生 | 116/116 | 00935-batch3-final.trx |
| 日志及固定优先级 | 修正后67/67 | 00935-guards-final.trx |
| 值列表／终态历史竞争 | 73/73、25/25 | 00935-pre-final.trx、00935-history-final.trx |
| 图标／身份恢复与Shell相关 | 112/112 | 00935-icon-identity-final.trx |
| 最终广泛Debug | 1199/1199，零失败／跳过 | 00935-final-bounded-debug.trx |
| 最终广泛Release | 1199/1199，零失败／跳过 | 00935-final-bounded-release.trx |
| 普通文件操作反馈跟进 | 84/84，Debug最新编译通过 | 00935-final-feedback-debug.trx、00935-final-feedback-debug-build.log |
| 非打包x64 Debug／Release | 通过 | 00935-final-feedback-debug-build.log、00935-final-release-build.log |
| x64／ARM64 Release签名MSIX | 内容、签名核验通过；包含SyncExcluded.png | 00935-final-package-x64.log、00935-final-package-arm64.log |
| ARM64测试交叉编译 | 通过，未实机执行 | 00935-final-tests-arm64-build.log |
| 实际Compile登记 | 主210／测试287，无重复或共享源遗漏 | 00935-final-compile-audit.json |

Debug广泛回归完成后仅跟进普通文件固定／释放的排除原因文字及对应断言，已通过最新Debug针对性；Release广泛回归包含该跟进。最终广泛运行使用独立runsettings限制4个测试线程，TRX诊断确认生效；产品超时及上传策略未为测试放宽。

初始失败保留：69项中v5预期未更新；Debug首轮1187/1191涉及记录相等性、v5断言和终态先通知后落库，均修正。后续高并发1194项中出现Shell属性超时、预览工作器超时、容量计时、LiveWebDavRunner未进入首轮／报告替换失败，原始00935-final-debug、serial-release等记录仍在；独立复测21/21、16/16及相关92/92通过。图标首轮59项3失败及63项2失败暴露对占位Shell状态的过严检查和属性事务完成等待；修正原生读回分支与共享读取，未改变InSync。此前最终图标Debug／Release各1196/1198：两次共同的改名日志问题已按FileId修正，另分别观察到原生资源占用0x800700AA及Shell发布超时；最新广泛与相关复测通过。身份清理探测的INVALID_PARAMETER原始失败也保留，最终策略保留模糊身份日志，不把它冒充已确认删除。

为尊重用户停止0.0.9.33专项验证，广泛过滤FailedMountTests、OrphanedMountRecoveryTests及ClearRemovalTests；不能称为不带过滤的全仓库全套通过。

## 产物

| 架构 | 相对仓库路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.35_x64_Test/HybridMount_0.0.9.35_x64.msix | 6F1D7F7C41EDB435EC9BEDAF4A9B18AE8905C0DB4CD154042A99D4FF8B33AE86 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.35_arm64_Test/HybridMount_0.0.9.35_arm64.msix | EF91CE24EA7311BC0DB9DA67FF2FD95A1D43F9625E5F628E0544130B8F0E4EB2 |

## 待测与交付边界

- 两个配置窗口的实际ListView编辑、窄窗口同排按钮、Tooltip、草稿／校验／保存反馈、页面切换与固定卡片行高。
- 用户已目视发现排除状态显示为普通文档图标，图标验收未通过；0.0.9.36将两种PNG评估／替换为真实多尺寸ICO和Shell资源引用，并复核实际渲染。API读回与资源存在不代表图标显示正确。
- 最终正式MSIX安装／原位升级、非打包实际启动及ARM64实机；不将交叉编译或签名验证写成实机通过。
- 正式服务兼容未由工具执行；保留此前版本桌面待办。现场用户自行部署的0.0.9.35仍有已知图标问题；下一版另增加任务页冲突处理。
- 完成后独立本地提交，保留产物与原始证据，不推送。

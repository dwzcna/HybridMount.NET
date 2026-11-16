# Microsoft Store 提交材料

本目录对应当前 Store 上架准备。文稿已准备不等于已经公开托管、通过认证或上架；所有实测结论以对应版本的 Validation 文档为准。0.0.25.1 兼容清理与0.0.25.2许可核对完成后，才准备统一 1.0.0.0 候选包。

本项目原创代码采用[MIT](../../LICENSE)，第三方和微软二进制分发条件见[许可矩阵](../ThirdParty-Licenses.md)。实际包须保留同一许可原文集合，源码MIT不重新许可第三方组件。

## 已确认的发行约定

| 项目 | 值 |
| --- | --- |
| 产品名称 | HybridMount.NET |
| 商店／未来侧载 MSIX Identity Name | `RyleD.HybridMount.NET` |
| Publisher | `CN=B14891C7-3483-4FD8-AC49-EA066227CC77` |
| PublisherDisplayName | `RyleD` |
| 收费 | 免费，无内购、订阅或试用到期限制 |
| Store 首发 | Release、Packaged、Full，x64／ARM64 |
| 其他渠道 | 保留 Full／Small MSIX 与目录／ZIP |
| 系统范围 | Windows 11 21H2 及以上；实际设备验收单独记录 |
| 正式版本 | 各组件与渠道统一 `1.0.0.0`，后续 Store 版本第四段保持 0 |

0.0.25.x 是准备版本。不能仅把清单改成 1.0.0.0、保留旧二进制版本后作为正式候选提交；不能在两个独立 Store 产品之间混用身份。实际包族名由系统返回。

## 文稿与发布状态

| 内容 | 简体中文 | 繁体中文 | English | 当前状态 |
| --- | --- | --- | --- | --- |
| 隐私政策 | [zh-CN](Privacy.zh-CN.md) | [zh-HK](Privacy.zh-HK.md) | [en-US](Privacy.en-US.md) | 发布前文稿；公开网址与支持联系方式待提供 |
| 商店文案 | [zh-CN](Listing.zh-CN.md) | [zh-HK](Listing.zh-HK.md) | [en-US](Listing.en-US.md) | 待核对最终候选的实际功能与截图 |
| 认证说明 | [认证步骤与权限用途](Certification.md) | 同左 | 同一文档含英文提交文稿 | 测试服务与账号待提供；没有填入生产凭据 |

隐私政策可作为准备版的应用内离线说明。正式提交前应公开托管与候选版本一致的文本，再填入 Partner Center 的真实 HTTPS 地址；应用中不能放置假网址、占位邮箱或无法访问的支持入口。

## 提交前必须补齐

- 隐私政策的公开 HTTPS 地址，确认无需登录即可查看，页面内容与应用内说明一致。
- RyleD 的实际支持网页或支持联系方式，核对其在商店产品页上的展示。隐私政策中的联系说明以该真实入口为准。
- 审核期间持续可访问的专用测试服务、账号及使用步骤。账号仅放在 Partner Center 的认证备注或受控交接中，不写入仓库、截图或日志；审核结束后撤销或轮换。
- x64／ARM64 正式候选的文件清单、摘要、统一版本、包身份、符号及 WACK 结果；无法执行的测试保留为未完成。
- 最终候选的真实截图，至少覆盖首页、挂载配置、资源管理器占位文件、任务详情及设置。使用虚构文件名和隔离测试数据，隐藏账号、访问密钥、服务器私有地址及个人路径；不得用示意图替代运行截图。
- 按实际功能填写年龄分级问卷、发行地区、免费定价和系统要求。类别拟使用“实用工具与工具／Utilities & tools”，以 Partner Center 实际提供的对应类别为准。
- 根据实际交付清单复核 .NET、Windows App Runtime、CommunityToolkit、SQLite／SQLitePCLRaw、AWS SDK、WebDAVClient、nlohmann/json 及其他随包依赖的许可证；保留必要声明，不以本清单代替逐包核对。

正式提交前选择“手动发布／审核通过后保留”一类受控发布安排，并由发布者确认实际提交与公开时间。准备工作不创建付费商品、不接入付款、不上传包。

## 旧用户切换与 0.0.25.1 数据基线

1. 在能读取原数据的版本中处理冲突、未确认上传及其他未决任务；另存不可丢失的本地内容。
2. 导出 `.hmxbk`；如需保密，为备份设置密码并单独保管密码。配置备份不包含文件正文、缓存、同步索引或任务恢复快照。
3. 在原版本中移除挂载。选择“保留已缓存的文件”只保留可完整保留的本地内容，不补下载未缓存文件；失败或阻止项处理完成前不要卸载。
4. 退出并卸载原 `dwzcn.HybridMount.NET` 包。新身份不接管它的数据、注册或管道；两个身份不按可并存的同一产品发行。
5. 使用新版本建立全新配置，逐项导入现行 `.hmxbk`，重新核对服务器与本地空目录并认证。保留下来的普通文件目录不自动重新作为同步根；需要时选择另一空目录。

0.0.25.1 使用 SQLite schema 9，不自动升级旧库。遇到旧库、较新格式、损坏或访问失败应保留现场并停止业务初始化；“允许重新建库”不授权应用自动删除用户原库。仍需使用原版本完成其持有的旧任务和挂载收尾。不要为卸载遗留问题递归删除未知目录或按文件名前缀删除远端对象。

## 验收和发布边界

Store 对 `runFullTrust` 的批准、Microsoft 重签后的启动、实际 Store 安装更新与 WACK 均是独立验证项。现有构建成功、普通签名和包内容检查不能替代这些结论。需要单独验证干净系统、标准用户、两架构、21H2、Explorer、DPI、高对比度、直接卸载和中断恢复。

准备版可以提交本地代码和文稿；公开隐私网页、创建外部测试服务、上传商店以及公开发布在取得相应授权和必要信息后进行。

## 官方依据

- [MSIX 包要求与版本规则](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)
- [MSIX Store 签名与外部分发签名](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)
- [受限能力及审批说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations#restricted-capabilities)
- [Microsoft Store 政策](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)
- [认证备注及受限能力用途](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/manage-submission-options)

以上依据于 2026-11-16 核对；正式提交前再次确认 Partner Center 当时的要求。

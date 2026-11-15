# 0.0.22.1 验收记录

## 实现与版本边界

本版保留主程序及Tests的目录、分层、Commons契约、UI渲染、共享夹具和结构门禁。Ext／Stock源码与工程布局恢复到52cda0f（0.0.21.0），不包含NativeShared；既有原生修复保留，三个模块仍按集中版本重编译。公共IPC v2、SQLite schema8、注册表配置、.hmxbk和四工程身份保持。

用户要求先缓存当前修改，恢复22.0并提交后再继续本版。已保存1044文件ZIP、原暂存索引、Git树和差异；恢复前后逐文件SHA-256及暂存树一致。证据：artifacts/validation/00221-work/cache-before-00220-commit/manifest.json、cache-restoration-verified.json。22.0已提交为18b184a，历史源码及189项直接Debug验证见docs/Validation-0.0.22.0.md，不改写其结果。

## 同代码前序验证

以下结果保留真实21.1编号，不能宣称为22.1二进制直接验证：回退后最小x64 Debug构建通过，主程序／Stock／Ext FileVersion 21.1；组合Debug564/564、TLS定向14/14通过。完整功能Release2163/2165、2失败、24.9681分钟：回环PUT连接15秒超时保留结果未知，Shell状态发布取消。证据在00211-work的debug-final-564.trx、tls-authority-isolation-debug.trx、release-final-2165.trx和release-final-result.json。原始TRX不改名伪装当前产物。

22.0恢复后的Debug189/189包含上述两个失败类，结果仅属于22.0。当前直接构建、定向Release复验、独立性能、双架构签名包及测试交叉编译完成后记录。

## 过程诊断

原22.0初轮完整功能Release2152/2162、10失败；权限模板参数遗漏已修正，WinRT根枚举COM查询失败与内容安全断言分开。TLS1.2／1.3同进程共用主机名触发Schannel协商缓存问题，使用不同回环主机名隔离后14/14通过。证书／ALPN／会话恢复试验已撤销，生产TLS和系统策略未改。两个使用共享CFAPI／Shell或130项物理缓存的测试类加入已有Native IPC独占集合，用例内并发、场景和性能阈值保持。未为通过用例重放结果未知PUT或放宽生产超时。

## 当前直接验证

| 检查 | 实际结果及证据 |
|---|---|
| x64 Debug非打包 | 构建成功；主程序／Stock／Ext FileVersion均22.1。minimal-x64-debug-build.log、debug-binary-versions.json |
| x64 Release定向 | **189/189**，包含架构、日志边界、WebDAV重定向／TLS、备份、Shell状态、原生启动／握手／传输、MUI及导航。release-targeted-189.trx；生产及隔离测试模块、测试程序集版本均22.1，见release-x64-binary-versions.json |
| 前序两项失败复验 | 上述189项中回环非寻址PUT和dirty Shell发布均通过；完整21.1运行失败仍保留，不宣称完整门禁一次通过 |
| 独立Release验收 | **4/4**。1000／10000项各100次暖态，固定7查询；P95分别**11.663／148.572ms**，P99分别13.237／218.733ms。沿用150／750ms阈值。acceptance-release-4.trx、acceptance-release.log |
| x64 MSIX | 22.1自包含包签名及验签成功；修正检查脚本后，同一已签包内容检查通过。sign-x64.log、signature-x64.log、package-check-x64.log、package-x64.sha256.txt |
| ARM64 | Release自包含签名包、验签和包检查通过；测试工程交叉编译0警告0错误。release-arm64-package.log、signature-ARM64.log、tests-arm64-cross-build.log |

包检查首轮在签名成功后因脚本仍指向旧XBF路径而失败。根据源码路径映射及实际PRI，将窗口、五页和五个弹窗共11条URI调整为Views/Windows、Views/Pages、Views/Dialogs；全部15项嵌入XBF的唯一性检查保持，未删除资源断言。同一签名包重查通过，无需重跑行为测试或重新签名。原失败日志为release-x64-targeted-package.log，映射见package-resource-path-fix.json。

source-delta-audit.json确认与精确缓存相比，主程序／测试无新增语义变动，版本和空白整理分别列出；包检查另只调整上述资源路径。因此按用户指令复用明确标号的同代码覆盖，不机械重跑完整相同集合。当前直接189项和4项集合分别记录，不与前序564／2165项相加。

包内三个模块最终逐一读取版本资源，x64／ARM64均为0.0.22.1，并保存模块及包SHA-256；见final-package-versions-and-hashes.json。交叉编译不执行ARM64代码。原生构建保留MUI覆盖警告，ARM64结构检查还报告工具链LIB包含不存在的atlmfc/ARM64路径（CS1668）；命令成功，不记录为零警告。包签名沿用本机证书，未添加时间戳。

## 未执行与保全

完整产品鼠标／键盘、实际WinUI／Explorer、VS密度现场、高对比度／跨屏DPI、真实AWS／DSM／WebDAV／代理、Windows11 21H2和ARM64实机待测。交叉编译不代表设备验收。保留原TaskListItem排版补丁和用户Plan-0.0.20.3.md；不推送、不安装生产包、不操作正式挂载、不重启Explorer。

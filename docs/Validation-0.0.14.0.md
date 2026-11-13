# 0.0.14.0 验收记录

基线 master / 7b7c997（0.0.13.0）；证据保存在 artifacts/validation/00140-work/。本版只读分析所得热点与隔离验收分开，不操作正式挂载，不重启 Explorer。

## 实现及边界

扫描在提交事务内重验观测、影子、本地发布证据和未完事件，连续无变化重连复用任务，不升级弱验证器；缺失本地发布继续修复，真实增量继续推进观测。旧积压逐项核验，不删除历史。

任务列表共用一次读取上下文，SQL查询数固定5；JSON解析在锁外，等待原因使用路径／祖先索引，交互及不可逆提交重新精确核验。上传及远端表增加查询索引，不引入跨所有调用的长驻连接。兼容 Populate 在原生动作前后仍逐项保存准备／回执，主发现流保留256项事务批次。

批量结果使用List、250ms／256项保存／通知，终态即时落盘；配置不变时不重复配置预览。缓存单后台增量统计，64路径批查只读元数据、五分钟校准，手动请求完整核对。四严重程度InfoBar使用Alpha FF实体画刷；高对比度引用系统色。受管路径选择以结构化结果给出所属挂载及移除入口，真实故障不吞掉。

## 自动回归

| 范围 | 结果 | 证据 |
| --- | --- | --- |
| x64 Debug 主矩阵 | 152/152，0失败／跳过 | targeted-debug-final.trx |
| x64 Release 扩展矩阵 | 213/213，0失败／跳过 | targeted-release-final.trx |
| 追加861条旧积压，Debug／Release | 各1/1 | backlog-debug.trx、backlog-release.trx |
| 最终扫描进度清理，Debug定向 | 26/26 | final-progress-debug.trx、final-progress-debug.log |
| 最终扫描进度清理，Release定向 | 26/26，0失败／跳过 | final-progress-release.trx、final-progress-release.log |
| ARM64 Release 测试交叉编译 | 0警告／0错误，未运行 | cross-tests-arm64.log |

追加积压夹具使用1775个DAV条目，将861条已核验事件回置Pending模拟旧积压；重新检测后全部完成、记录数和文件身份不变、没有GET／PUT／DELETE，限定30秒内收尾。不是访问生产 D:\wdavtest 的结果。最终补充确保扫描失败／等待应用取消时临时进度清理，完整152／213矩阵不是该局部补充后的重新执行。

Release暖态100样本（前三次预热不计）仓储快照：1000项P95=48.673ms、P99=73.990ms；10000项P95=526.098ms、P99=597.283ms。Debug相应P95=23.469／402.816ms。每次5条查询，覆盖祖先与跨作用域屏障。这不是完整任务页面、UI取消或Explorer性能测量。

## 实际 WinUI

独立 InfoBarProbe 使用生产共享样式子集与原生模板，没有启动正式应用或访问正式数据。infobar-ui.json 记录24组通过：三语言×明暗×四严重程度，实际ContentRoot背景Alpha=255、控件／画刷Opacity=1；另有6次动作和6次关闭重开。infobar-light.png、infobar-dark.png 为实际RenderTargetBitmap。

高对比度仅核对资源使用系统颜色，没有切换操作系统高对比度做视觉验收。未将代码检查当作该项通过。

## 构建与产物

最小及最终x64 Debug非打包通过；最终x64 Release非打包通过（unpackaged-release-final.log）；x64 Release与ARM64 Release签名MSIX及包检查通过。最终包包含扫描进度清理补充，证据package-x64-final.log、package-arm64-final.log；签名使用既有CN=dwzcn／指纹3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0，没有新增信任或安装正式包。

| 架构 | 产物 | SHA-256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount.NET_0.0.14.0_x64_Test/HybridMount.NET_0.0.14.0_x64.msix | 84FD08910A9C59B4E5764842FC393E86A05451688146AA0717A97F916B468336 |
| ARM64 | artifacts/packages/ARM64/HybridMount.NET_0.0.14.0_ARM64_Test/HybridMount.NET_0.0.14.0_ARM64.msix | 9AEC6698028C775755BE2F84C8338BDE4D1987F68A0A5FCB7AD5C94577691251 |

## 中间失败与修正

首次相关回归9项因隔离宿主仍为旧版本失败，重新构建同版隔离宿主后通过，未移除版本校验。结构化返回的泛型推断、测试辅助类遗漏已修正。批量释放回归发现父行结果列表未同步，修正后包含于152／213项通过记录。首次打包源manifest仍为.13，统一.14后最终双架构包通过。

InfoBar独立验证初轮遇到Application字段初始化和缺少App.xaml／PRI的问题，修正验证程序后得到最终通过JSON；保留失败文件，不将probe-startup-error.txt视为最终结果。所有产品文本经版本化补丁检查／应用，应用后删除补丁，副本留在证据目录。

## 待测试

生产挂载重连及现场规模，真实UI交互／取消P95、Explorer端到端响应；高对比度、DPI、读屏、完整主题切换；真实DSM、Windows11 21H2、ARM64实机。没有运行ARM64测试，没有补跑.33停止专项。公共IPC v2、.hmxbk、安装身份及四工程保持；本地提交，不推送。

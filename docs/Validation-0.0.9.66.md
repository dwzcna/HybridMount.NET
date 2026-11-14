# 0.0.9.66 验收记录

基线aad0e0a（.65），分支codex/native-backend；保留四工程、HybridMount.exe及当时的预览身份。实现、回归和打包分开记录，不将旧源码产物当最终验证。

## 实现

GetState仅内存摘要及最多16个已有PIDL，不同步访问文件系统／CFAPI。fOkToBeSlow为false不派发；未知返回E_PENDING。完整选择及根身份／文件ID／内容元数据核验由双线程有界后台处理，队列连同在途最多16项，最多1024项／256Ki字符，250ms摘要、取消代次及500ms闲置线程退出。根菜单、枚举器与子命令共享Context，封送流只领取一次，模块保持至后台和COM引用收尾。

Invoke重新核验全部项与动作资格，再经既有IPC v2派发，前16项签名仅作观察键。基础身份摘要可补齐动作能力；非云普通文件不调用昂贵Shell排除属性，Invoke对所有相关项逐个检查。只读14项计数导出不访问源；生产拒绝测试延迟入口。

## 最终自动验证

- x64 Debug／Release各30/30、0跳过，覆盖慢采样、缓存／待处理预算、摘要能力补全、第17项外部目录拒绝、40项完整Invoke、身份替换、满队列取消、重复及超限选择和已有通知／预览／真实CFAPI保护。日志和TRX：artifacts/validation/00966-menu/delivery-*。
- 最小x64编译、双配置非打包、x64／ARM64 Release签名MSIX及包检查、ARM64测试交叉编译通过，最终包哈希见final-packages.json。
- 独立MSIX最终证据：desktop3ColdMenu66-dc8a7956；主程序未运行，进程外真实COM／IPC。冷激活62.5237ms单列；单冷文件1000次P95 0.0885ms／P99 0.1728ms／最大0.4758ms，无待处理；千项选择1000次P95 0.4859ms／P99 0.6358ms／最大1.3555ms，114次E_PENDING，包含到期未命中，不能称全部命中。均满足P95≤20ms／P99≤50ms。
- 实际Invoke传递1000个已验证完整路径、150106字节；原生宿主无CLR。冷内容、文件身份、Pin、InSync及正文对照保持，宿主退出无剩余子进程；测试包卸载后原生根、注册及Namespace均不存在。仅清理本轮GUID隔离根。

## 阶段故障与边界

首次独立包因跨进程Shell数组接口地址不稳定导致摘要一直待处理，改用有界已有PIDL观察签名后解除。第二轮千项选择被逐项Shell属性查询拖过预算，按动作分级并对普通文件跳过属性处理器后第三轮成功。两轮失败证据分别保存在desktop3ColdMenu66-ea80f9a2、desktop3ColdMenu66-3157e6d0，均已清理本轮根、卸载独立包并完成包外注销核验。只读本轮宿主计数及阶段采样保存在menu-counters-readonly.json／menu-phase-times.json；未访问正式进程。

此前29项交付结果在跨进程修正前生成，保存在delivery-before-final-*，仅作阶段证据。最新首轮Debug29/30，NativeOutOfProcessComReturnsCachedBitmapAfterMainMountDisconnects在finally删除缩略图库时报告身份变化；精确单项1/1及最终完整双配置各30/30通过，原失败保存在phase-cleanup-failure-*，原因仍未确认，未改弱产品删除身份检查。实际COM缓存读取断言没有失败。

人工Explorer完整菜单交互、真实设备过滤驱动／长时间使用、正式升级和ARM64实机尚未执行。没有操作正式挂载、重启Explorer或补跑.33停止专项；仅本地提交，不推送、不改master。

## 产物

D:/src/HybridMount.NET/artifacts/packages/ARM64/HybridMount_0.0.9.66_arm64_Test/HybridMount_0.0.9.66_arm64.msix

SHA256：EA9C3055352C1F8CCBCFD93B02CED8410AB3AD374CB8CFC1717941B28B2285A6

D:/src/HybridMount.NET/artifacts/packages/x64/HybridMount_0.0.9.66_x64_Test/HybridMount_0.0.9.66_x64.msix

SHA256：F65FDFED520A8FC981B00DCF6225E8224E8E3C676EC78CB6165EBAA65B52D125

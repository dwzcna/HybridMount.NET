# 0.0.17.1 验收记录

基线a0c4f0d；证据及版本化补丁位于artifacts/validation/00171-work/。本地提交，不推送。

## 实现

WebDAV系统证书验证失败先拒绝，在当前挂载连接对话框提供证书详情、确认重试与清除。确认绑定草稿代次、预期服务器及叶证书SHA-256；输入变化立即失效，保存连接后保留指纹，系统验证成功仍正常接受。连接与测试共用客户端策略，不修改系统信任。证书拒绝不被超时覆盖；主动取消保持取消，TLS／网络分别分类。公开证书详情不序列化到公共IPC，.hmxbk的JSON／加密方式和本地配置格式不变。

## 已测试

- 最终x64 Debug非打包及同版隔离宿主：build-test-debug-final.log；首次该阶段50/50通过。
- 扩大Debug／Release各78/78，0失败、0跳过：debug-final2.trx、release-final.trx；涵盖本机随机端口真实TLS、过期／名称不符证书、详情与指纹、明确确认后的OPTIONS／PROPFIND、独立挂载拒绝、替换证书、系统信任、草稿ABA、用户取消、网络／TLS／超时分类、保存后连接失败保留配置及安全序列化、协议与备份回归。夹具不安装证书信任，不使用生产地址。
- x64 Release非打包：build-test-release-final.log；最终UI候选为空保护补充后x64 Debug重新编译通过（build-debug-final2.log），两架构Release自包含签名MSIX／包检查通过（package-x64-final2.log、package-arm64-final2.log），哈希见packages-final.sha256.json。末次仅补充按钮候选为空的防护，未更改已通过的协议逻辑。
- ARM64测试程序集交叉编译通过，0警告／0错误：tests-arm64-build.log；不等于实机执行。
- 三语言新增9键（1215键×3；原生8×3），旧证书说明同步；git diff --check。

## 阶段失败与待测

首次页面结果回归用到0.0.17.0隔离Stock，因与0.0.17.1主程序版本不符被正确拒绝（debug-final.trx：48通过、1失败）；重建同版隔离宿主后50/50，再扩大到两配置各78/78通过。失败日志保留，不放宽版本校验。

完整产品提示布局及按钮交互、三语言视觉、高对比度、跨屏DPI、Windows11 21H2、ARM64实机及生产服务器待测。未安装产品包，未操作正式挂载或重启Explorer。生产观察到的ParentPackageMismatch另在0.0.18.0修复，不作为本版TLS验证已解决事项。

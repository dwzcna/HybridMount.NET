# 0.0.9.58 WebDAV HTTPS 验收

基于973fbd6（.57），指定补丁原文先经git apply --check后应用；配套改动同样经过受检补丁。保留四工程、HybridMount.exe及当时的预览身份。日志保存artifacts/validation/00958-webdav-https/。

## 实现

独立HTTPS新建入口、5006指引、挂载级SHA-256指纹及原v6备份；未配置指纹使用系统验证，错误指纹不可重试绕过，地址修改撤销旧指纹。未更改系统证书存储。

## 已测试

- x64 Debug最小非打包、x64 Release非打包构建通过。
- 最终x64 Debug／Release针对性各102/102、0跳过；targeted-*-final.trx及log。覆盖HTTPS端口／中文路径、指纹规范化、实际TLS只读OPTIONS及PROPFIND、默认不信任、跨挂载隔离与证书更换拒绝，以及超时／取消和配置备份。
- x64／ARM64 Release MSIX均使用既有测试证书签名，签名及包内容核验通过；*-Release-Package-final2.log。ARM64测试工程Release交叉编译0警告0错误，不代表实机运行。
- 首轮89/102，13项失败由隔离原生后台仍为.57触发版本保护；重建隔离映像后两配置均通过。最初打包发现源清单版本未同步，随后源清单已为.58；ARM64在同期工具栏编辑期间遇到XAML重复Content，最新源码恢复合法后双架构重建通过。所有阶段日志保留。
- 构建前后source-before-final.json／source-after-final.json记录同期界面文件摘要。工具栏图标为用户同期工作区修改，提交仅纳入HTTPS菜单项；交付MSIX使用最终构建时的工具栏界面，不将未提交界面差异冒充提交内容。

## 待测试

真实DSM设备、人工配置窗口／Explorer、正式安装升级及ARM64实机；不将交叉编译当实机通过。

## 签名产物

- x64：`artifacts/packages/x64/HybridMount_0.0.9.58_x64_Test/HybridMount_0.0.9.58_x64.msix`，SHA256 `0a9f5f6b816e8e3f63172e8d410ba33832298b2ac73d72fd23f353050fe7a827`。
- arm64：`artifacts/packages/ARM64/HybridMount_0.0.9.58_arm64_Test/HybridMount_0.0.9.58_arm64.msix`，SHA256 `61cd19d717a34905cd150af17701de228c6346dc74d94d981b703d811c5ccf59`。

# HybridMount 0.0.9.24 验收记录

2026-11-06，Windows x64。[PR #16更新](https://github.com/dwzcna/HybridMount.NET/pull/16)，独立双父合并，累计第19次版本递增。

## 合并及冲突处理

- 来源a0911b2291c4e87cc3e81256be5302c71ea69e8c，父节点为原#16提交78b99e5及已发布master 807d554；合并前master为e6eb9fb6b9efd98180f971720a4a6c2688defb2a。
- PR #16来源a0911b2回并旧master，产品实现已在0.0.9.21合入；新测试等待与当前Drain适配器等价，解决冲突后完整代码／测试树与0.0.9.23一致。保留当前上传及移动后等待，不引入新的产品或测试改动；本次仍独立合并并递增第四段版本。
- Git merge-tree预演后只对ProbePermissionFallbackTests保留当前等价且更完整的等待，解决后整棵树与合并前HEAD树一致，记录ResolvedTree=1111a440156295c46d17ade935e62792516d1189；实际产品与测试均无新增差异。历史合并建立第二父节点，版本、清单及README通过受检补丁独立升至0.0.9.24。

## 本版实际验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64 Debug最小WinUI／原生 | 通过 | pr16-followup1-minimal.log |
| 探测作用域／权限回退针对性 | 51/51，零失败／跳过 | pr16-followup1-targeted.log／trx |
| x64 Release及x64／ARM64签名MSIX | 通过签名与内容核验 | pr16-followup1-release.log、arm64-package.log |
| ARM64测试 | 仅交叉编译通过，未实机运行 | pr16-followup1-arm64-tests-build.log |
| x64非打包Release | 构建通过，EXE／程序集版本均0.0.9.24；未启动／安装 | pr16-followup1-unpackaged.log、unpackaged-version.json |
| 实际Compile登记 | 主186／测试248，相关41／68文件，无重复／遗漏 | pr16-followup1-compile-audit.json |
| Debug／Release全量 | 本版未重复；相同代码树的0.0.9.23各1033/1033仅作上版证据 | pr18-evidence.json、pr18-debug.trx、pr18-release.trx |

首次编译审计脚本误将TasksPage.xaml.cs要求到无头测试工程；已纠正审计预期并核对该页面由主工程实际编译，未修改工程登记，初次审计原因独立保留。

## 分支与发布

- PR #1—#18及本次#16更新的来源提交均由独立合并保留，第四段版本连续0.0.9.6—0.0.9.24。
- 本地来源分支在完成提交后逐个删除；已在GitHub master的旧15个远端来源已删除，证据pr01-15-github-branch-cleanup.json。新三个远端来源在master尚未发布前保留，当前GitHub master为807d554；本批仅本地提交，不推送master或Azure origin。
- 最终版本链／祖先／工作区／包摘要审计为pr-merges-final-audit-round2.json；本版独立机器证据为pr16-followup1-evidence.json。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.24_x64_Test/HybridMount_0.0.9.24_x64.msix | FDDF4214098F590AE2E3B178D0996A87E0B0526737F480DA5F2F09C731845167 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.24_arm64_Test/HybridMount_0.0.9.24_arm64.msix | F813E815EAD2B1CF1D56D097BAAB2CC05634E406BF66E8DC72040622EEADDA04 |

真实服务、GUI／Explorer、安装升级和ARM64实机未执行，不访问已取消NAS，不操作正式挂载、配置和用户文件。PR7无可靠UploadId自动精确回收仍未实现，日志保留待核对；不猜测中止外部上传。

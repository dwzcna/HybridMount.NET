# 0.0.9.56 GitHub PR 收尾核对

2026-11-10用户要求处理已整合PR及来源分支。初始GitHub codex/native-backend为0753a66（.55），与本地基线一致；GitHub master为7461bff、本地master为ba424e1，本轮均不改动。只在本地提交.56代码，不推送目标分支。证据artifacts/validation/00956-main-name/。

## 已整合功能与GitHub状态

下列16项已按原计划修订、分批本地整合，相关提交已由用户发布到GitHub .55。但GitHub原PR均未标记Merged，不能把功能已移植称为PR已通过GitHub merge。

| PR | 已交付版本／提交 | 当前来源HEAD | 本轮状态 |
| --- | --- | --- | --- |
| [#22](https://github.com/dwzcna/HybridMount.NET/pull/22) | .46／866f0db | aeeceef817e3e2c37fb73d108efcc122a8e4b733 | Closed；来源分支已删除；merged_at为空 |
| [#23](https://github.com/dwzcna/HybridMount.NET/pull/23) | .46／866f0db | 2c26e47d9579b32cb9730546ce8cead6e855e617 | 来源分支已删除，GitHub自动Closed；merged_at为空 |
| [#24](https://github.com/dwzcna/HybridMount.NET/pull/24) | .49／ab0eecd | d3a7e54a19121464c4cbdceaf65a1081f98e22b1 | Closed；来源分支已删除；merged_at为空 |
| [#25](https://github.com/dwzcna/HybridMount.NET/pull/25) | .49／ab0eecd | d53109f6f594fd937750508e6034e5adb1a31463 | Closed；来源分支已删除；merged_at为空 |
| [#26](https://github.com/dwzcna/HybridMount.NET/pull/26) | .48／f40d99b | 670dff0b4f218a111139134ab48356f7fb84e7b0 | Closed；来源分支已删除；merged_at为空 |
| [#27](https://github.com/dwzcna/HybridMount.NET/pull/27) | .46／866f0db | 5651294371197ce21e969988b32daad29f4622a6 | Closed；来源分支已删除；merged_at为空 |
| [#28](https://github.com/dwzcna/HybridMount.NET/pull/28) | .46／866f0db | 620e495c1ed6f3d8985bcbad72a2f51270ea450f | Closed；来源分支已删除；merged_at为空 |
| [#29](https://github.com/dwzcna/HybridMount.NET/pull/29) | .50／24be9c5 | 247df8897371c9321e03c61a8692e29c0446553d | Closed；来源分支已删除；merged_at为空 |
| [#30](https://github.com/dwzcna/HybridMount.NET/pull/30) | .51／d2a389d | c20f114c0c2cdde2e8aed08ac6af89499a488d68 | Closed；来源分支已删除；merged_at为空 |
| [#31](https://github.com/dwzcna/HybridMount.NET/pull/31) | .52／ea6c254 | d84d3b3df351b7fb61fb9c0d7b8930b1da06c8c6 | Closed；来源分支已删除；merged_at为空 |
| [#32](https://github.com/dwzcna/HybridMount.NET/pull/32) | .48／f40d99b | 2047f03d1741cd2664b243ca7b78cb0f63f45f1d | Closed；来源分支已删除；merged_at为空 |
| [#33](https://github.com/dwzcna/HybridMount.NET/pull/33) | .49／ab0eecd | 39727a4cb885b20761689da45af20e12b1df4de1 | Closed；来源分支已删除；merged_at为空 |
| [#34](https://github.com/dwzcna/HybridMount.NET/pull/34) | .52／ea6c254 | 0ef62a438b9b40051190edae9d3f54586c7b4fa5 | Closed；来源分支已删除；merged_at为空 |
| [#35](https://github.com/dwzcna/HybridMount.NET/pull/35) | .48／f40d99b | 28b8a14d065fab86f86a2bdadeebc9b983e346c4 | Closed；来源分支已删除；merged_at为空 |
| [#36](https://github.com/dwzcna/HybridMount.NET/pull/36) | .50／24be9c5 | 08d5652068f6ea84855c72578d019f289b5ebbfc | Closed；来源分支已删除；merged_at为空 |
| [#37](https://github.com/dwzcna/HybridMount.NET/pull/37) | .51／d2a389d | 34092cf0a0da0400a41a0902e94876707610473b | Closed；来源分支已删除；merged_at为空 |

#26、#30、#32、#37最新HEAD是原来源与.46基线866f0db的合并提交；逐项查看相对.46的功能差异，仍对应已交付功能，安全修订保留。#34来源SHA与原实现一致。完整初始快照、有效差异及核对证据保留，不套用原基线的旧clean状态。

首项#23在删除精确来源分支后被GitHub自动关闭，merged_at仍为空。源2c26e47d9579b32cb9730546ce8cead6e855e617保存在本地refs/archive/native-pr-23-00956，可恢复；目标分支没有因此增加代码提交。

## 已执行及最终核验

用户随后明确授权关闭仍Open的#22、#24—#37并删除来源。15项操作已完成；加上此前#23，本轮共16项Closed及16个来源删除。此前自动审批曾因没有明确关闭Open PR的授权而拒绝；取得本次明确授权后重新执行，没有绕过拒绝。

执行前将固定HEAD归档到本地refs/archive/native-pr-<编号>-00956；核对来源仓库、当前提交、已发布目标包含整合提交，且没有其他Open PR依赖该来源。只发送针对固定SHA的删除租约，未推送.56代码、重写目标分支历史或创建GitHub merge提交。删除后有短暂状态传播延迟，逐项等待并使用新鲜REST查询确认Closed，merged_at均为空；原始一次过早查询失败记录保留。

最终远端仅3个分支：

- codex/native-backend：0753a66d932525e306134b77b77c6758ddc331bf（未变）。
- master：7461bff1334fdfd0677bd7c07635c5ca658cfc55（未变）。
- devin/1794198120-sync-root-icon-refresh：9574a9a59a2f47f8d3514ab67e16870231259a14（#21，未变）。

GitHub仅#21仍Open，目标为master，本轮保留PR与来源分支；本地master仍为ba424e1faad8b45bb0f298a3993ba63778371990。原生.56图标引用补充不代表原C# master PR已在其目标分支合并。此前#1—#20来源在初始快照均已不存在。

实际证据：github-cleanup-first.json（#23）、github-cleanup.json及cleanup-github-resume.log（其余15项、最终3个分支和Open PR清单）。本地归档可用于恢复来源，GitHub状态记录为Closed，功能已在.46／.48—.52移植，不宣称GitHub Merged。

# 0.0.22.1｜仅整理.NET与Tests

按用户最新指令，保留主程序与测试的目录、分层、契约及夹具整理；Ext／Stock以52cda0f为基线恢复原源码和工程布局，撤销本轮NativeShared，不回退此前已提交的原生修复。

主程序保留Commons、Models、Services、Infrastructure、Composition、Helpers、UI及Views分类，ViewModel统一后缀；不新增DI框架或Abstractions。保留外部格式、SQLite schema8、.hmxbk、公共IPC v2及四工程身份。

测试保留单工程及Unit／Integration／Native／Architecture／Fixtures／Acceptance／Assets，集中生产链接与共享夹具，不机械删减场景。构建结构检查继续启用，按恢复后的原生实际路径核对，不关闭检查。

回退前先保存精确原生源码、补丁和路径备份；只处理本轮新原生源码及空目录，不触及bin／obj和其他改动。更新根AGENTS为主程序／Tests规则，明确原生保持21.0布局。

重新执行回退后的最小x64、针对性Debug／Release及必要双架构／签名包检查。前序各版证据保留真实编号，不作为本版直接产物验证。当前版直接证据存artifacts/validation/00221-work；前序21.1证据保留在00211-work并标明原版本，验证后本地提交，不推送、不安装生产包、不操作正式挂载或重启Explorer。

先前22.0四工程整理已按用户指令本地提交为18b184a。当前缓存完整恢复后继续本版，原生源码和工程布局以52cda0f为准；本版相对22.0的原生回退属于明确授权范围。历史22.0文档保留。

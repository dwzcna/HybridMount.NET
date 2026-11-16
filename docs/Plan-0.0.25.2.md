# 0.0.25.2｜添加 MIT 并核对第三方许可

接0.0.25.1独立实现、验证及本地提交。不推送、不上传Store、不安装生产包或信任、不操作正式挂载和Explorer。

- 根目录添加标准英文MIT，作者dwzcn、2026；包Publisher RyleD不替代作者。README链接本许可及第三方声明。
- 核对应用直接／传递、原生源码、测试／构建及Full／Small运行时的准确解析版本；区分再分发与开发／系统前置条件，不把微软二进制统称MIT，不重新许可第三方资产。
- 沿用25.0同一ThirdParty/Resolved集合，保留原始LICENSE／NOTICE；补充实际遗漏的WinUI工具NOTICE、SQLitePCLRaw对应版本NOTICE和有固定来源的AWS原文。原文不改写翻译，来源与责任写清。
- MSIX、目录和ZIP携带根LICENSE与同一第三方文稿；两类交付门禁共用逐字节SHA256核验。根LICENSE纳入构建输入摘要，防止复用旧许可载荷。
- 文档给出准确版本矩阵、Apache专利／NOTICE条件、微软及MPL范围，不以关键词扫描捏造GPL或专有组件；不删功能规避许可。
- 最小x64与定向Debug／Release，ARM64交叉编译，双架构Full／Small的Sideload及Store容器检查；使用隔离副本签名，不安装。验证许可缺失／篡改拒绝，实际版本与边界记录在Validation和artifacts/validation/00252-work/。
- 原业务、IPC、schema9、本机配置标记和.hmxbk编码保持。WinUI／Explorer、DPI、高对比、21H2、ARM64设备、WACK及Store重签待测。1.0候选须接本版核对后另行准备，不在本轮生成。

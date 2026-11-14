# 0.0.18.2 验证记录

基于b1425d5。仅修改WebDAV表示摘要及相关传输／测试；四工程、公共IPC、SQLite结构和.hmxbk不变。版本字段加入可选表示摘要，不把弱ETag升级为条件验证器。

证据目录artifacts/validation/00182-work/。

## 实际验证

- 初次编译发现基线通知机械替换多余括号，修正后测试工程编译通过。
- 首轮27项有6项在夹具Dispose核验时因旧0.0.18.0原生宿主版本不符而失败；重建同版隔离宿主后，扩大Debug183项中182通过，唯一旧测试仍期待OPTIONS先行。修正该断言后，摘要／HTTPS／容量相关Debug25/25通过，见digest-debug-final.trx。
- 真实本机HTTP验证SHA-256／512、同大小时间内容变化、弱版本不启用Range／If-Match、完整GET头与分块尾摘要篡改、缓存不发布、HEAD持续冲突一次重读、上传快照与目标HEAD匹配后零正文回读。
- 字典夹具覆盖合法未知算法／参数／逗号字符串、算法优先、重复成员／参数、坏Base64、错误长度与截断语法。
- 最小x64 Debug／Release主程序及同版隔离原生编译通过；最终测试／交付结果另附。

## 待验证

实际服务器／代理的RFC9530通告、分块尾部行为与性能，完整产品WinUI、Windows11 21H2及ARM64设备未执行。未操作正式挂载、不重启Explorer、不安装生产包。源读取失败和摘要错误不推进快照，摘要仅作内容核验，仍须已有条件／锁保护才能修改远端。

## 最终结果与交付

最终Release扩大WebDAV矩阵183/183、0失败0跳过，见webdav-release-final.trx；Debug末次定向25/25，未冒称扩大Debug矩阵全部重跑。两架构Release自包含签名MSIX、验签和包检查通过，见package-x64.log、package-arm64.log；ARM64测试程序集仅交叉编译通过，0警告／0错误，见tests-arm64-build.log。未安装生产包。SHA256见packages.sha256.json，补丁0.0.18.2.patch；验证记录后独立本地提交，不推送。

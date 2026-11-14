# 0.0.18.4 验证记录

基于4e9689a。范围为WebDAV PROPFIND／REPORT的gzip协商和手动解压，保留四工程、现有身份、公共IPC、SQLite及.hmxbk格式。预计源码与隔离验证约0.5—1工作日，设备及人工验收另计。

证据artifacts/validation/00184-work/。

## 实际验证

最小x64 Debug非打包及同版隔离原生构建通过；Debug定向45/45、零失败零跳过。真实HTTP验证仅目录／REPORT协商gzip，GET／HEAD为identity、PUT正文原字节；成功解析后才有能力证据。独立流验证坏CRC、长度、头、额外尾部及平台可能容忍的截断、实际64MiB展开上限，XML的16Mi字符／DTD保护继续有效。坏REPORT保持原检查点，慢元数据正文服从既有截止。最终网络额度补充及Release／交付另附。

实现不缓存展开正文，跟随原响应生命周期释放底层流；CRC及ISIZE用于完整EOF验证，不对提前关闭宣称成功。文件正文和范围安全、RFC9530、权限预检保持。

## 待验证

真实WebDAV／代理gzip兼容、生产规模性能、完整产品WinUI、Windows11 21H2和ARM64实机。未安装生产包、未操作正式挂载、不重启Explorer；隔离服务不能作为生产验收。

网络额度补充首轮因测试引用了错误的TasksQueueService命名空间而未编译，修正后Debug独立用例1/1通过（lease-debug-final.trx）：全局额度为1，解码失败后队列为空，随后正常请求立即完成；未用扩大Release编译失败冒称测试运行。

最终Release扩大矩阵274/274、0失败／0跳过，见metadata-release-final.trx，含WebDAV、RFC6578、能力及调度器；Debug45/45及网络额度追加1/1。实际坏REPORT不推进检查点、单全局额度无泄漏，未重复无变化的完整Debug扩大矩阵。

双架构Release自包含签名MSIX、验签与包检查通过，见package-x64.log、package-arm64.log；ARM64测试交叉编译0警告／0错误，见tests-arm64-build.log，未执行ARM64测试。SHA256见packages.sha256.json，版本补丁0.0.18.4.patch。验证记录后独立本地提交，不推送，包未安装。

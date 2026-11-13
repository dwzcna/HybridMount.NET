#pragma once
#include "RuntimeHost.h"
namespace hm {
/// <summary>原生目录监听与任务意图所有者；IOCP只复制解析入队，管道和业务回执使用独立线程。</summary>
class TaskDispatcher {
    struct Impl;std::unique_ptr<Impl> _impl;
public:
    explicit TaskDispatcher(RuntimeHost& host);~TaskDispatcher();
    /// <summary>已核验根连接后、初次扫描前启动监听；每根一个64KiB缓冲，不读取正文。</summary>
    void Add(const Json& root);
    /// <summary>停止捕获并交付最终补扫意图，返回必须由.NET持久接纳的最后序号。</summary>
    long long Remove(const std::string& session);
    /// <summary>仅接纳认证任务管道的固定回执及结果；不在此执行文件操作。</summary>
    void Receive(const Json& frame);
    Json Statistics()const;
    void Stop();
#ifdef HYBRIDMOUNT_NATIVE_TESTS
    void OverflowForTests(const std::string& session);
#endif
};
}

#pragma once
#include "RuntimeHost.h"
namespace hm {
/// <summary>隔离观察进程：控制／心跳、目录识别STA与UIA MTA分离；只读本产品视图，不访问正文。</summary>
int RunPreviewObserver(const Json& roots,bool focusOnly=false) noexcept;
/// <summary>本地异步队列通知前使用的非阻塞焦点摘要；查询只登记待通知根，识别在隔离进程内执行。</summary>
bool ThumbnailNoticeForeground(const std::wstring& path,const Json& grant) noexcept;
/// <summary>宿主退出停止自己拥有的前台目录探针及工作进程，重复调用幂等。</summary>
void StopThumbnailNoticeProbe() noexcept;
#ifdef HYBRIDMOUNT_NATIVE_TESTS
/// <summary>隔离策略回归入口；生产映像不提供此接口或命令行角色。</summary>
Json TestForegroundObserver(const Json& input);
#endif
/// <summary>一个运行时共享的观察子进程；失败仅停止预览提示，根连接和关键回调不受影响。</summary>
class PreviewObserver {
    struct Impl;std::unique_ptr<Impl> _impl;
public:
    explicit PreviewObserver(RuntimeHost& host);~PreviewObserver();
    void Configure(const Json& root,bool enabled);
    void Remove(const std::string& session);
    void Stop();
    /// <summary>只复制父进程最近心跳计数；不启动观察、Shell查询或生成。</summary>
    Json Statistics()const;
};
}

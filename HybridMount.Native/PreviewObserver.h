#pragma once
#include "RuntimeHost.h"
namespace hm {
/// <summary>独立STA浏览观察进程；只读Explorer视图，输出有界元数据提示，不访问媒体正文。</summary>
int RunPreviewObserver(const std::vector<std::string>& mounts) noexcept;
/// <summary>一个运行时共享的观察子进程；失败仅停止预览提示，根连接和关键回调不受影响。</summary>
class PreviewObserver {
    struct Impl;std::unique_ptr<Impl> _impl;
public:
    explicit PreviewObserver(RuntimeHost& host);~PreviewObserver();
    void Configure(const Json& root,bool enabled);
    void Remove(const std::string& session);
    void Stop();
};
}

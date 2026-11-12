#pragma once
#include "RuntimeHost.h"
namespace hm {
/// <summary>隔离观察进程：控制／心跳、目录识别STA与UIA MTA分离；只读本产品视图，不访问正文。</summary>
int RunPreviewObserver(const Json& roots) noexcept;
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

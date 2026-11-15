#pragma once
#include "NativeCommon.h"
namespace hm {
/// <summary>冷／部分文件未命中只零等待投递；后台核验同会话同部署应用，绝不激活主程序。</summary>
void QueueRemoteThumbnailHint(const std::wstring& path,UINT size,const std::string& stamp,const Json& grant) noexcept;
/// <summary>查询内存中的待投递状态及计数，不探测服务、不排队或读取远端。</summary>
bool RemoteThumbnailHintsPending() noexcept;
Json RemoteThumbnailHintStatistics();
/// <summary>停止接纳、取消本宿主的管道IO并等待发送线程；重复调用幂等。</summary>
void StopRemoteThumbnailHints() noexcept;
}

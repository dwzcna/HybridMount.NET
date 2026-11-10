#pragma once
#include "NativeCommon.h"
namespace hm {
/// <summary>只读本次请求授权后查询内存结果或合并后台需求；不等待解码，队列满时直接未命中。</summary>
std::vector<BYTE> QueueLocalThumbnail(const std::wstring& path,const std::string& stamp,const Json& grant);
/// <summary>供Shell空闲退出判断；只查内存，包含解码及尚未发出的单文件通知。</summary>
BOOL WINAPI LocalThumbnailWorkPending(void*) noexcept;
/// <summary>关闭入口后取消本宿主的队列并等待工作线程归还自己创建的进程；可重复调用。</summary>
void StopLocalThumbnails() noexcept;
/// <summary>只返回内存计数，不启动后台线程或查询文件。</summary>
Json LocalThumbnailStatistics();
}

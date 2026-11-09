#pragma once
#include "NativeCommon.h"
namespace hm {
/// <summary>当前线程只读查询阶段，用于无路径诊断；不保存用户内容。</summary>
extern thread_local const char* ThumbnailReadStage;
/// <summary>显式只读诊断的元数据比较证据，不含路径或媒体内容。</summary>
extern thread_local Json ThumbnailEvidence;
/// <summary>当前部署的每挂载SQLite缓存直读；不创建文件、不发IPC、不启动主程序或读取媒体正文。</summary>
std::vector<BYTE> ReadCachedThumbnail(const std::wstring& path,UINT size);
/// <summary>只读根级授权；没有缓存行时仍可核验完整本地文件，禁用、损坏或其他根均拒绝。</summary>
Json ReadThumbnailGrant(const std::wstring& path);
/// <summary>供原生COM宿主使用的内存回调；返回内存由调用者CoTaskMemFree。</summary>
HRESULT WINAPI ReadThumbnail(void*,const wchar_t* path,UINT size,BYTE** png,DWORD* length) noexcept;
}

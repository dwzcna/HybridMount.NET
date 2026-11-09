#pragma once
#include "ThumbnailCache.h"
namespace hm {
/// <summary>缓存优先，完整本地内容在独立原生进程中回退；冷文件不启动工作进程或水合。</summary>
std::vector<BYTE> ReadAvailableThumbnail(const std::wstring& path,UINT size);
/// <summary>固定本地工作角色；核验启用根及完整内容，结果经匿名标准输出返回。</summary>
int RunLocalThumbnailWorker(const std::wstring& path,UINT size,HANDLE acknowledged) noexcept;
}

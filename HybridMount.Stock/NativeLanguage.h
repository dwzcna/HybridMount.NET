#pragma once
#include <windows.h>
namespace hm::language {
/// <summary>固定原生展示键；编号与三种随包MUI和托管资源检查一致。</summary>
enum class Text : unsigned { Product, FreeSpace, KeepOnDevice, ShowSyncIssues, IssuesTip, CacheTip, SyncIssues, UploadExcluded, Count };
/// <summary>只返回进程内不可变文字；热路径只读原子缓存和已打开的共享摘要，不读磁盘、注册表或CLR。</summary>
const wchar_t* Get(Text text) noexcept;
/// <summary>返回缓存LCID供只读诊断，不触发业务、网络或正文读取。</summary>
unsigned Current() noexcept;
}

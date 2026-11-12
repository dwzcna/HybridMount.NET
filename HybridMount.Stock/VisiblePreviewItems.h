#pragma once
#include "NativeCommon.h"
#include <shobjidl.h>
namespace hm {
/// <summary>只读视口采样；不导航、不选择、不实现虚拟项目，也不读取文件正文。失败样本不能当作空目录。</summary>
struct PreviewViewItems {Json Paths=Json::array();int Candidates{};ULONGLONG Milliseconds{};bool Complete{};HRESULT Error=S_OK;};
/// <summary>只读MTA活动时间；用于拥有者回收持续不合作的隔离进程，不启动采样。</summary>
bool PreviewSamplerUncooperative()noexcept;
PreviewViewItems ReadVisiblePreviewItems(IShellView* view,IFolderView2* folder,const std::wstring& directory,ULONGLONG deadline=0);
}

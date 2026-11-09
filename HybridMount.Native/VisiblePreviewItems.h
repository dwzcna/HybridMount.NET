#pragma once
#include "NativeCommon.h"
#include <shobjidl.h>
namespace hm {
/// <summary>只读视口采样；不导航、不选择、不实现虚拟项目，也不读取文件正文。失败样本不能当作空目录。</summary>
struct PreviewViewItems {Json Paths=Json::array();int Candidates{};bool Complete{};HRESULT Error=S_OK;};
PreviewViewItems ReadVisiblePreviewItems(IShellView* view,IFolderView2* folder,const std::wstring& directory);
}

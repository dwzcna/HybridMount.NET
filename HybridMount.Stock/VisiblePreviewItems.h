#pragma once
#include "NativeCommon.h"
#include <shobjidl.h>
#include <atomic>
namespace hm {
/// <summary>采样的共享取消证据；控制线程撤销，MTA在每次UIA/文件名解析前只读核对。</summary>
struct PreviewViewGuard {
    HWND Frame{},View{};DWORD Process{};std::atomic<bool> Canceled{};
    bool Current()const noexcept{DWORD pid{};return !Canceled&&Frame&&GetForegroundWindow()==Frame&&IsWindowVisible(Frame)&&!IsIconic(Frame)&&GetWindowThreadProcessId(Frame,&pid)&&pid==Process&&(!View||(IsWindowVisible(View)&&GetAncestor(View,GA_ROOT)==Frame));}
};
/// <summary>只读视口采样；不导航、不选择、不实现虚拟项目，也不读取文件正文。失败样本不能当作空目录。</summary>
struct PreviewViewItems {Json Paths=Json::array();int Candidates{};ULONGLONG Milliseconds{};bool Complete{};HRESULT Error=S_OK;};
/// <summary>只读MTA活动时间；用于拥有者回收持续不合作的隔离进程，不启动采样。</summary>
bool PreviewSamplerUncooperative()noexcept;
PreviewViewItems ReadVisiblePreviewItems(IShellView* view,IFolderView2* folder,const std::wstring& directory,ULONGLONG deadline=0,std::shared_ptr<PreviewViewGuard> guard={});
}

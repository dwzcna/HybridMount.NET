#pragma once
#include "Extension.h"
#include <memory>
#include <array>
namespace hm::menu {
enum class Action {Root,FreeSpace,KeepOnDevice,ShowSyncIssues};
/// <summary>后台取得的文件身份、根代次和状态版本；查询线程只读取已经完成的不可变摘要。</summary>
struct Item {
    std::wstring Path,RootRevision;FILE_ID_INFO Identity{};FILE_BASIC_INFO Basic{};long long Size{};DWORD Actions{};bool Valid{};
};
struct Summary {HRESULT Status=E_PENDING;GUID Mount{};DWORD Actions{},CheckedActions{};std::vector<Item> Items;};
/// <summary>只在MTA工作线程核验完整集合；每16项让出执行，最多1024项／256Ki字符，不读正文。</summary>
std::shared_ptr<const Summary> InspectSelection(IShellItemArray* selection,IUnknown* site,const std::atomic<bool>& canceled,Action action,bool invoke);
/// <summary>根菜单、枚举器和子命令共用一次选择查询；失效／析构只请求取消，不等待文件系统。</summary>
class Context {
    struct Impl;std::unique_ptr<Impl> _impl;
public:
    Context();~Context();
    HRESULT State(Action action,IShellItemArray* selection,IUnknown* site,BOOL slow,EXPCMDSTATE* state)noexcept;
    HRESULT Invoke(Action action,IShellItemArray* selection,IUnknown* site)noexcept;
    void Invalidate()noexcept;
};
/// <summary>固定数值诊断；不查询磁盘、注册表、CFAPI或源。</summary>
extern std::array<std::atomic<unsigned long long>,14> Counters;
/// <summary>测试映像可注入单项慢采样，生产恒为零。</summary>
DWORD ProbeDelay()noexcept;
}

#pragma once
#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <wrl.h>
#include <atomic>
#include <string>
#include <vector>
#include "../HybridMount.Stock/NativeIdentity.h"

#ifdef HYBRIDMOUNT_SHELL_PROBE_ID
inline constexpr CLSID HybridMountCommandId{HYBRIDMOUNT_SHELL_PROBE_ID^0x10000000,0x4055,0x4f23,{0x8c,0xc0,0x91,0xb7,0x36,0xb3,0xe4,0x91}};
#else
inline constexpr CLSID HybridMountCommandId{ 0x59964fc1,0xd076,0x4dc1,{0x97,0x78,0x01,0xf5,0x83,0x3a,0x5d,0x4a} };
#endif
/// <summary>仅隔离Shell试验记录无路径的原生阶段；正式DLL无注册表写入或追踪副作用。</summary>
inline void TraceShellProbe(const wchar_t* stage) noexcept {
#ifdef HYBRIDMOUNT_SHELL_PROBE_TAG
#define HM_PSTRING2(value) #value
#define HM_PSTRING(value) HM_PSTRING2(value)
#define HM_PWIDE2(value) L##value
#define HM_PWIDE(value) HM_PWIDE2(value)
    HKEY key{};
    if(RegCreateKeyExW(HKEY_CURRENT_USER,L"Software\\HybridMount.NET.Tests\\ShellProbe\\" HM_PWIDE(HM_PSTRING(HYBRIDMOUNT_SHELL_PROBE_TAG)),0,nullptr,0,KEY_SET_VALUE,nullptr,&key,nullptr)==ERROR_SUCCESS){DWORD pid=GetCurrentProcessId();RegSetValueExW(key,stage,0,REG_DWORD,reinterpret_cast<const BYTE*>(&pid),sizeof(pid));RegCloseKey(key);}
#else
    UNREFERENCED_PARAMETER(stage);
#endif
}
extern HMODULE g_module;
extern std::atomic<long> g_objects;
/// <summary>所有 COM 对象共享模块存活计数，后台线程另外持有系统模块引用。</summary>
struct ModuleLifetime {
	ModuleLifetime() noexcept { ++g_objects; }
	~ModuleLifetime() { --g_objects; }
	ModuleLifetime(const ModuleLifetime&) = delete;
	ModuleLifetime& operator=(const ModuleLifetime&) = delete;
};
/// <summary>异步发送占位请求；输入已复制，不在工作线程使用 Explorer 的 COM 对象。</summary>
HRESULT QueuePlaceholderRequest(bool keepOnDevice, std::vector<std::wstring> paths) noexcept;

/// <summary>异步只读定位同步问题；菜单回调不联网、不修改文件、不启动上传。</summary>
HRESULT QueueSyncIssuesRequest(std::vector<std::wstring> paths) noexcept;

// 真实Shell自动选择测试使用本轮独立CLSID，不改变正式已安装类的激活解析。
#ifdef HYBRIDMOUNT_SHELL_PROBE_ID
inline constexpr CLSID HybridMountThumbnailId{HYBRIDMOUNT_SHELL_PROBE_ID,0x4d1c,0x4c7e,{0xa5,0x19,0x34,0xc7,0x6b,0x9b,0xb2,0x81}};
#else
inline constexpr CLSID HybridMountThumbnailId{0xa749b295,0x9cb5,0x4f08,{0x98,0xd4,0x00,0xeb,0x47,0x34,0x5e,0x09}};
#endif
/// <summary>验证本机根身份，不打开文件内容；异常视为不归属。</summary>
bool IsOwnedMountPath(const std::wstring& path,GUID& mount) noexcept;
/// <summary>同一 DLL 内创建缩略图对象，由共享模块计数管理生命周期。</summary>
using HybridMountReadThumbnail=HRESULT(WINAPI*)(void* context,const wchar_t* path,UINT size,BYTE** png,DWORD* length);
/// <summary>宿主缓存回调的借用上下文；工厂、对象和活动调用均结束后宿主才释放。</summary>
struct ThumbnailCacheSource{HybridMountReadThumbnail Read{};void* Context{};std::atomic<bool>* Closing{};};
HRESULT CreateThumbnailHandler(REFIID iid,void** result,const ThumbnailCacheSource* source=nullptr) noexcept;
/// <summary>创建本产品根菜单；状态查询仅读取本机属性，实际动作由应用服务执行。</summary>
HRESULT CreateMenuHandler(REFIID iid,void** result) noexcept;
/// <summary>flags位0为缩略图、位1为菜单；独立STA宿主不创建WinUI或同步服务。</summary>
extern "C" HRESULT WINAPI HybridMountRunShellServer(HybridMountReadThumbnail read,void* context,HANDLE stopEvent,DWORD idleMilliseconds,DWORD flags) noexcept;
/// <summary>注册唯一缩略图类的本地COM服务；不创建菜单、不运行主应用，空闲时安全撤销。</summary>
extern "C" HRESULT WINAPI HybridMountRunThumbnailServer(HybridMountReadThumbnail read,void* context,HANDLE stopEvent,DWORD idleMilliseconds) noexcept;

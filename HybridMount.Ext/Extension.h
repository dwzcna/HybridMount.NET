#pragma once
#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <wrl.h>
#include <atomic>
#include <string>
#include <vector>

inline constexpr CLSID HybridMountCommandId{ 0xf4a8fd0b,0x4055,0x4f23,{0x8c,0xc0,0x91,0xb7,0x36,0xb3,0xe4,0x91} };
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

inline constexpr CLSID HybridMountThumbnailId{0xa65802eb,0x4d1c,0x4c7e,{0xa5,0x19,0x34,0xc7,0x6b,0x9b,0xb2,0x81}};
/// <summary>缓存查询共用 150 毫秒截止时间；服务缺失时直接失败，绝不激活主程序。</summary>
bool QueryCachedPreview(const std::wstring& path,UINT size,std::vector<BYTE>& png) noexcept;
/// <summary>验证本机根身份，不打开文件内容；异常视为不归属。</summary>
bool IsOwnedMountPath(const std::wstring& path,GUID& mount) noexcept;
/// <summary>同一 DLL 内创建缩略图对象，由共享模块计数管理生命周期。</summary>
HRESULT CreateThumbnailHandler(REFIID iid,void** result) noexcept;

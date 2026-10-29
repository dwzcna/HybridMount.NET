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

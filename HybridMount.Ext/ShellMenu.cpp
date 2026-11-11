// 菜单生命周期和类工厂基于 Microsoft ExplorerCommand 示例改写。
// Copyright (c) Microsoft Corporation. All rights reserved.
// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND,
// EITHER EXPRESSED OR IMPLIED, INCLUDING MERCHANTABILITY OR FITNESS FOR A PARTICULAR PURPOSE.
#include "Extension.h"
#include "MenuQuery.h"
#include <shlguid.h>
#include <new>
#include <winternl.h>
#include <cfapi.h>
#include <windows.data.json.h>
#include <wrl/wrappers/corewrappers.h>
#include <roapi.h>
#include <cstddef>
#include <sddl.h>
#include <algorithm>
#include <memory>
#include <propsys.h>
static_assert(offsetof(CF_SYNC_ROOT_STANDARD_INFO, SyncRootIdentity) == 1056);
static_assert(offsetof(CF_PLACEHOLDER_STANDARD_INFO, FileIdentity) == 60);
static_assert(sizeof(CF_CALLBACK_INFO) == 152);
static_assert(sizeof(CF_OPERATION_INFO) == 48);
static_assert(sizeof(CF_OPERATION_PARAMETERS) == 48);

using namespace Microsoft::WRL;
HMODULE g_module = nullptr;
std::atomic<long> g_objects{ 0 };
namespace {
	using Operation=hm::menu::Action;
	class Command;
	/// <summary>每次枚举拥有独立游标；Clone 复制位置但不共享可变枚举状态。</summary>
	class CommandEnumerator final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IEnumExplorerCommand>, private ModuleLifetime {
		ULONG _index = 0;
		ComPtr<IUnknown> _site;std::shared_ptr<hm::menu::Context> _context;
	public:
		CommandEnumerator(IUnknown* site,std::shared_ptr<hm::menu::Context> context,ULONG index=0):_index(index),_site(site),_context(std::move(context)){}
		IFACEMETHODIMP Next(ULONG count, IExplorerCommand** commands, ULONG* fetched) override;
		IFACEMETHODIMP Skip(ULONG count) override {
			const auto available = 3 - _index;
			_index += count < available ? count : available;
			return count <= available ? S_OK : S_FALSE;
		}
		IFACEMETHODIMP Reset() override { _index = 0; return S_OK; }
		IFACEMETHODIMP Clone(IEnumExplorerCommand** result) override {
			if (!result) return E_POINTER;
			*result = nullptr;
			auto clone = Make<CommandEnumerator>(_site.Get(),_context,_index);
			return clone ? clone.CopyTo(result) : E_OUTOFMEMORY;
		}
	};
	/// <summary>根菜单与缓存和问题定位子命令；所有文件系统目标只在 Invoke 中复制。</summary>
	class Command final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand, IObjectWithSite>, private ModuleLifetime {
		Operation _operation;
		ComPtr<IUnknown> _site;std::shared_ptr<hm::menu::Context> _context;
	public:
		Command(Operation operation,IUnknown* site=nullptr,std::shared_ptr<hm::menu::Context> context={}):_operation(operation),_site(site),_context(context?std::move(context):std::make_shared<hm::menu::Context>()){}
		IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override {
			if (!title) return E_POINTER;
			return SHStrDupW(_operation == Operation::Root ? L"HybridMount.NET" : _operation == Operation::FreeSpace ? L"释放空间" : _operation == Operation::KeepOnDevice ? L"始终保留在此设备上" : L"查看同步问题", title);
		}
		IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override {
			if (!icon) return E_POINTER;
			*icon = nullptr;
			try {
				std::wstring path(32768, L'\0');
				const auto count = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
				if (!count || count >= path.size()) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
				path.resize(count);
				path += L",-101";
				return SHStrDupW(path.c_str(), icon);
			}
			catch (...) { return E_OUTOFMEMORY; }
		}
		IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* text) override {
			if (!text) return E_POINTER;
			return SHStrDupW(_operation == Operation::ShowSyncIssues ? L"在应用中定位此文件或目录的同步问题；不会自动上传。" : L"管理此挂载的离线缓存；本地修改会受到保护。", text);
		}
		IFACEMETHODIMP GetCanonicalName(GUID* name) override {
			if (!name) return E_POINTER;
			*name = HybridMountCommandId;
			name->Data4[7] += static_cast<BYTE>(_operation);
			return S_OK;
		}
        /// <summary>同步调用只读内存摘要；未知返回E_PENDING，后台核验不使用借用的COM接口。</summary>
        IFACEMETHODIMP GetState(IShellItemArray* selection,BOOL slow,EXPCMDSTATE* state)override{return _context->State(_operation,selection,_site.Get(),slow,state);}
        /// <summary>排入独立有界工作，执行前重新核验完整集合，不能靠菜单缓存授权变更。</summary>
        IFACEMETHODIMP Invoke(IShellItemArray* selection,IBindCtx*)override{return _context->Invoke(_operation,selection,_site.Get());}
		IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override {
			if (!flags) return E_POINTER;
			*flags = _operation == Operation::Root ? ECF_HASSUBCOMMANDS : ECF_DEFAULT;
			return S_OK;
		}
		IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** result) override {
			if (!result) return E_POINTER;
			*result = nullptr;
			if (_operation != Operation::Root) return E_NOTIMPL;
			auto enumerator = Make<CommandEnumerator>(_site.Get(),_context);
			return enumerator ? enumerator.CopyTo(result) : E_OUTOFMEMORY;
		}
		IFACEMETHODIMP SetSite(IUnknown* site) override { _site = site;_context->Invalidate();return S_OK; }
		IFACEMETHODIMP GetSite(REFIID iid, void** site) override {
			if (!site) return E_POINTER;
			*site = nullptr;
			return _site ? _site.CopyTo(iid, site) : E_FAIL;
		}
	};
	HRESULT CommandEnumerator::Next(ULONG count, IExplorerCommand** commands, ULONG* fetched) {
		if (!commands || (!fetched && count != 1)) return E_POINTER;
		if (fetched) *fetched = 0;
		for (ULONG index = 0; index < count; ++index) commands[index] = nullptr;
		ULONG written = 0;
		while (written < count && _index < 3) {
			auto command = Make<Command>(_index == 0 ? Operation::FreeSpace : _index == 1 ? Operation::KeepOnDevice : Operation::ShowSyncIssues,_site.Get(),_context);
			if (!command) {
				for (ULONG index = 0; index < written; ++index) { commands[index]->Release(); commands[index] = nullptr; }
				_index -= written;
				return E_OUTOFMEMORY;
			}
			command.CopyTo(&commands[written]);
			++written; ++_index;
		}
		if (fetched) *fetched = written;
		return written == count ? S_OK : S_FALSE;
	}
	/// <summary>仅创建本产品根命令，拒绝聚合；服务锁与对象计数共同控制 DLL 卸载。</summary>
	class Factory final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IClassFactory>, private ModuleLifetime {
        bool _thumbnail;
	public:
        explicit Factory(bool thumbnail):_thumbnail(thumbnail){}
		IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** result) override {
			if (!result) return E_POINTER;
			*result = nullptr;
			if (outer) return CLASS_E_NOAGGREGATION;
            if(_thumbnail){TraceShellProbe(L"CreateInstance");return CreateThumbnailHandler(iid,result);}
			try{auto command=Make<Command>(Operation::Root);return command?command.CopyTo(iid,result):E_OUTOFMEMORY;}catch(...){return E_OUTOFMEMORY;}
		}
		IFACEMETHODIMP LockServer(BOOL lock) override { if (lock) ++g_objects; else --g_objects; return S_OK; }
	};
}
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
	if (reason == DLL_PROCESS_ATTACH) { g_module = module; DisableThreadLibraryCalls(module); }
	return TRUE;
}
STDAPI DllCanUnloadNow() { return g_objects.load() == 0 ? S_OK : S_FALSE; }
STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) {
    TraceShellProbe(L"DllGetClassObject");
	if (!result) return E_POINTER;
	*result = nullptr;
	if (clsid != HybridMountCommandId && clsid != HybridMountThumbnailId) return CLASS_E_CLASSNOTAVAILABLE;
	auto factory = Make<Factory>(clsid==HybridMountThumbnailId);
	return factory ? factory.CopyTo(iid, result) : E_OUTOFMEMORY;
}

HRESULT CreateMenuHandler(REFIID iid,void** result) noexcept {
    TraceShellProbe(L"CreateMenuHandler");
    if(!result)return E_POINTER;*result=nullptr;
    try{auto command=Make<Command>(Operation::Root);return command?command.CopyTo(iid,result):E_OUTOFMEMORY;}catch(...){return E_OUTOFMEMORY;}
}

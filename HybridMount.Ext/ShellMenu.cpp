// 菜单生命周期和类工厂基于 ProKit.Ext 所参考的 Microsoft ExplorerCommand 示例改写。
// Copyright (c) Microsoft Corporation. All rights reserved.
// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND,
// EITHER EXPRESSED OR IMPLIED, INCLUDING MERCHANTABILITY OR FITNESS FOR A PARTICULAR PURPOSE.
#include "Extension.h"
#include <shlguid.h>
#include <new>

using namespace Microsoft::WRL;
HMODULE g_module = nullptr;
std::atomic<long> g_objects{ 0 };
namespace {
	enum class Operation { Root, FreeSpace, KeepOnDevice };
	class Command;
	/// <summary>每次枚举拥有独立游标；Clone 复制位置但不共享可变枚举状态。</summary>
	class CommandEnumerator final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IEnumExplorerCommand>, private ModuleLifetime {
		ULONG _index = 0;
		ComPtr<IUnknown> _site;
	public:
		CommandEnumerator(IUnknown* site, ULONG index = 0) : _index(index), _site(site) {}
		IFACEMETHODIMP Next(ULONG count, IExplorerCommand** commands, ULONG* fetched) override;
		IFACEMETHODIMP Skip(ULONG count) override {
			const auto available = 2 - _index;
			_index += count < available ? count : available;
			return count <= available ? S_OK : S_FALSE;
		}
		IFACEMETHODIMP Reset() override { _index = 0; return S_OK; }
		IFACEMETHODIMP Clone(IEnumExplorerCommand** result) override {
			if (!result) return E_POINTER;
			*result = nullptr;
			auto clone = Make<CommandEnumerator>(_site.Get(), _index);
			return clone ? clone.CopyTo(result) : E_OUTOFMEMORY;
		}
	};
	/// <summary>从目录背景的视图站点取得真实文件系统目录；不枚举内容、不访问管道。</summary>
	HRESULT BackgroundPath(IUnknown* site, std::wstring& result) {
		if (!site) return E_FAIL;
		ComPtr<IFolderView> view;
		auto status = IUnknown_QueryService(site, SID_SFolderView, IID_PPV_ARGS(&view));
		if (FAILED(status)) {
			ComPtr<IShellBrowser> browser;
			ComPtr<IShellView> shellView;
			if (FAILED(IUnknown_QueryService(site, SID_STopLevelBrowser, IID_PPV_ARGS(&browser)))
				|| FAILED(browser->QueryActiveShellView(&shellView)) || FAILED(shellView.As(&view))) return E_FAIL;
		}
		ComPtr<IPersistFolder2> folder;
		status = view->GetFolder(IID_PPV_ARGS(&folder));
		if (FAILED(status)) return status;
		PIDLIST_ABSOLUTE pidl = nullptr;
		status = folder->GetCurFolder(&pidl);
		if (FAILED(status)) return status;
		PWSTR path = nullptr;
		status = SHGetNameFromIDList(pidl, SIGDN_FILESYSPATH, &path);
		CoTaskMemFree(pidl);
		if (SUCCEEDED(status)) { result = path; CoTaskMemFree(path); }
		return status;
	}
	/// <summary>根菜单与两个占位子命令；所有文件系统目标只在 Invoke 中复制。</summary>
	class Command final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand, IObjectWithSite>, private ModuleLifetime {
		Operation _operation;
		ComPtr<IUnknown> _site;
	public:
		Command(Operation operation, IUnknown* site = nullptr) : _operation(operation), _site(site) {}
		IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override {
			if (!title) return E_POINTER;
			return SHStrDupW(_operation == Operation::Root ? L"HybridMount.NET" : _operation == Operation::FreeSpace ? L"释放空间" : L"始终保留在设备上", title);
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
			return SHStrDupW(L"0.0.1 占位命令：记录请求，不修改文件。", text);
		}
		IFACEMETHODIMP GetCanonicalName(GUID* name) override {
			if (!name) return E_POINTER;
			*name = HybridMountCommandId;
			name->Data4[7] += static_cast<BYTE>(_operation);
			return S_OK;
		}
		IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override {
			if (!state) return E_POINTER;
			// 首版全局可见；此回调不查询网络、文件属性或 IPC。
			*state = ECS_ENABLED;
			return S_OK;
		}
		IFACEMETHODIMP Invoke(IShellItemArray* selection, IBindCtx*) override {
			if (_operation == Operation::Root) return S_OK;
			try {
				std::vector<std::wstring> paths;
				DWORD count = 0;
				if (selection && FAILED(selection->GetCount(&count))) return E_FAIL;
				if (count > 1024) return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
				for (DWORD index = 0; index < count; ++index) {
					ComPtr<IShellItem> item;
					PWSTR path = nullptr;
					if (FAILED(selection->GetItemAt(index, &item)) || FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path))) return E_INVALIDARG;
					paths.emplace_back(path);
					CoTaskMemFree(path);
				}
				if (paths.empty()) {
					std::wstring background;
					const auto status = BackgroundPath(_site.Get(), background);
					if (FAILED(status)) return status;
					paths.push_back(std::move(background));
				}
				return QueuePlaceholderRequest(_operation == Operation::KeepOnDevice, std::move(paths));
			}
			catch (...) { return E_OUTOFMEMORY; }
		}
		IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override {
			if (!flags) return E_POINTER;
			*flags = _operation == Operation::Root ? ECF_HASSUBCOMMANDS : ECF_DEFAULT;
			return S_OK;
		}
		IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** result) override {
			if (!result) return E_POINTER;
			*result = nullptr;
			if (_operation != Operation::Root) return E_NOTIMPL;
			auto enumerator = Make<CommandEnumerator>(_site.Get());
			return enumerator ? enumerator.CopyTo(result) : E_OUTOFMEMORY;
		}
		IFACEMETHODIMP SetSite(IUnknown* site) override { _site = site; return S_OK; }
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
		while (written < count && _index < 2) {
			auto command = Make<Command>(_index == 0 ? Operation::FreeSpace : Operation::KeepOnDevice, _site.Get());
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
	public:
		IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** result) override {
			if (!result) return E_POINTER;
			*result = nullptr;
			if (outer) return CLASS_E_NOAGGREGATION;
			auto command = Make<Command>(Operation::Root);
			return command ? command.CopyTo(iid, result) : E_OUTOFMEMORY;
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
	if (!result) return E_POINTER;
	*result = nullptr;
	if (clsid != HybridMountCommandId) return CLASS_E_CLASSNOTAVAILABLE;
	auto factory = Make<Factory>();
	return factory ? factory.CopyTo(iid, result) : E_OUTOFMEMORY;
}

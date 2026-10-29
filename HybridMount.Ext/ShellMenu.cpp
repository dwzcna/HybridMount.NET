// 菜单生命周期和类工厂基于 ProKit.Ext 所参考的 Microsoft ExplorerCommand 示例改写。
// Copyright (c) Microsoft Corporation. All rights reserved.
// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND,
// EITHER EXPRESSED OR IMPLIED, INCLUDING MERCHANTABILITY OR FITNESS FOR A PARTICULAR PURPOSE.
#include "Extension.h"
#include <shlguid.h>
#include <new>
#include <winternl.h>
#include <cfapi.h>
#include <windows.data.json.h>
#include <wrl/wrappers/corewrappers.h>
#include <roapi.h>
#include <cstddef>
#include <sddl.h>
static_assert(offsetof(CF_SYNC_ROOT_STANDARD_INFO, SyncRootIdentity) == 1056);
static_assert(offsetof(CF_PLACEHOLDER_STANDARD_INFO, FileIdentity) == 60);
static_assert(sizeof(CF_CALLBACK_INFO) == 152);
static_assert(sizeof(CF_OPERATION_INFO) == 48);
static_assert(sizeof(CF_OPERATION_PARAMETERS) == 48);

using namespace Microsoft::WRL;
HMODULE g_module = nullptr;
std::atomic<long> g_objects{ 0 };
namespace {
	enum class Operation { Root, FreeSpace, KeepOnDevice };
/// <summary>只读取本机 CFAPI 根身份；不访问服务或水合内容，未知或畸形信息隐藏菜单。</summary>
bool OwnedRoot(const std::wstring& path, GUID& mount) {
    if (path.size() < 3 || path[1] != L':' || PathIsUNCW(path.c_str())) return false;
    const std::wstring drive = path.substr(0, 3);
    if (GetDriveTypeW(drive.c_str()) == DRIVE_REMOTE) return false;
    std::vector<BYTE> buffer(8192);
    if (FAILED(CfGetSyncRootInfoByPath(path.c_str(), CF_SYNC_ROOT_INFO_STANDARD, buffer.data(), static_cast<DWORD>(buffer.size()), nullptr))) return false;
    const auto info = reinterpret_cast<const CF_SYNC_ROOT_STANDARD_INFO*>(buffer.data());
    if (info->SyncRootIdentityLength == 0 || info->SyncRootIdentityLength > 4096) return false;
    const auto data = reinterpret_cast<const char*>(info->SyncRootIdentity);
    const auto count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, data, static_cast<int>(info->SyncRootIdentityLength), nullptr, 0);
    if (count <= 0) return false;
    std::wstring json(count, L'\0');
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, data, static_cast<int>(info->SyncRootIdentityLength), json.data(), count)) return false;
    const auto initialized = RoInitialize(RO_INIT_SINGLETHREADED);
    struct RoScope { HRESULT status; ~RoScope() { if (SUCCEEDED(status)) RoUninitialize(); } } ro{ initialized };
    ComPtr<ABI::Windows::Data::Json::IJsonObjectStatics> factory;
    ComPtr<ABI::Windows::Data::Json::IJsonObject> root;
    using Microsoft::WRL::Wrappers::HString;
    using Microsoft::WRL::Wrappers::HStringReference;
    if (FAILED(RoGetActivationFactory(HStringReference(RuntimeClass_Windows_Data_Json_JsonObject).Get(), IID_PPV_ARGS(&factory))) || FAILED(factory->Parse(HStringReference(json.c_str()).Get(), &root))) return false;
    HString product, id;
    double schema = 0;
    if (FAILED(root->GetNamedString(HStringReference(L"Product").Get(), product.GetAddressOf())) || wcscmp(product.GetRawBuffer(nullptr), L"HybridMount.NET") != 0 ||
        FAILED(root->GetNamedNumber(HStringReference(L"Schema").Get(), &schema)) || schema != 1 ||
        FAILED(root->GetNamedString(HStringReference(L"MountId").Get(), id.GetAddressOf()))) return false;
    HString owner;
    if (FAILED(root->GetNamedString(HStringReference(L"Owner").Get(), owner.GetAddressOf()))) return false;
    HANDLE rawToken = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &rawToken)) return false;
    struct TokenScope { HANDLE value; ~TokenScope() { CloseHandle(value); } } token{ rawToken };
    DWORD tokenSize = 0;
    GetTokenInformation(token.value, TokenUser, nullptr, 0, &tokenSize);
    std::vector<BYTE> user(tokenSize);
    if (!GetTokenInformation(token.value, TokenUser, user.data(), tokenSize, &tokenSize)) return false;
    LPWSTR sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid, &sid)) return false;
    const auto sameUser = wcscmp(sid, owner.GetRawBuffer(nullptr)) == 0;
    LocalFree(sid);
    if (!sameUser) return false;
    auto guid = std::wstring(L"{") + id.GetRawBuffer(nullptr) + L"}";
    return SUCCEEDED(CLSIDFromString(guid.c_str(), &mount));
}
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
			return SHStrDupW(L"管理此挂载的离线缓存；本地修改会受到保护。", text);
		}
		IFACEMETHODIMP GetCanonicalName(GUID* name) override {
			if (!name) return E_POINTER;
			*name = HybridMountCommandId;
			name->Data4[7] += static_cast<BYTE>(_operation);
			return S_OK;
		}
		IFACEMETHODIMP GetState(IShellItemArray* selection, BOOL slow, EXPCMDSTATE* state) override {
        if (!state) return E_POINTER;
        *state = ECS_HIDDEN;
        if (!slow) return E_PENDING;
        try {
            std::vector<std::wstring> paths;
            DWORD count = 0;
            if (selection && FAILED(selection->GetCount(&count))) return S_OK;
            if (count > 1024) return S_OK;
            for (DWORD index = 0; index < count; ++index) {
                ComPtr<IShellItem> item; PWSTR raw = nullptr;
                if (FAILED(selection->GetItemAt(index, &item)) || FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &raw))) return S_OK;
                paths.emplace_back(raw); CoTaskMemFree(raw);
            }
            if (paths.empty()) { std::wstring background; if (FAILED(BackgroundPath(_site.Get(), background))) return S_OK; paths.push_back(std::move(background)); }
            GUID expected{}; bool first = true;
            for (const auto& path : paths) { GUID current{}; if (!OwnedRoot(path, current) || (!first && current != expected)) return S_OK; expected = current; first = false; }
            *state = ECS_ENABLED;
        } catch (...) { *state = ECS_HIDDEN; }
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

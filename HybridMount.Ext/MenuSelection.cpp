// 菜单元数据核验由原ShellMenu.cpp迁入，保留Microsoft ExplorerCommand示例来源。
// Copyright (c) Microsoft Corporation. All rights reserved.
#include "MenuQuery.h"
#include <shlguid.h>
#include <winternl.h>
#include <cfapi.h>
#include <algorithm>
#include <cwctype>
#include <winioctl.h>
#include <windows.data.json.h>
#include <wrl/wrappers/corewrappers.h>
#include <roapi.h>
#include <propsys.h>
#include <sddl.h>
#include <set>
using namespace Microsoft::WRL;
namespace {
/// <summary>只读取本机 CFAPI 根身份；不访问服务或水合内容，未知或畸形信息隐藏菜单。</summary>
bool OwnedRoot(const std::wstring& path, GUID& mount,std::wstring* revision=nullptr) {
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
    const auto initialized = RoInitialize(RO_INIT_MULTITHREADED);
    if(FAILED(initialized)&&initialized!=RPC_E_CHANGED_MODE)return false;
    struct RoScope { HRESULT status; ~RoScope() { if (SUCCEEDED(status)) RoUninitialize(); } } ro{ initialized };
    ComPtr<ABI::Windows::Data::Json::IJsonObjectStatics> factory;
    ComPtr<ABI::Windows::Data::Json::IJsonObject> root;
    using Microsoft::WRL::Wrappers::HString;
    using Microsoft::WRL::Wrappers::HStringReference;
    if (FAILED(RoGetActivationFactory(HStringReference(RuntimeClass_Windows_Data_Json_JsonObject).Get(), IID_PPV_ARGS(&factory))) || FAILED(factory->Parse(HStringReference(json.c_str()).Get(), &root))) return false;
    HString product, id;
    double schema = 0;
    if (FAILED(root->GetNamedString(HStringReference(L"Product").Get(), product.GetAddressOf())) || wcscmp(product.GetRawBuffer(nullptr), hm::ProductScope().c_str()) != 0 ||
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
    if(FAILED(CLSIDFromString(guid.c_str(), &mount)))return false;
    if(revision)*revision=guid+L":"+std::to_wstring(info->SyncRootFileId.QuadPart)+L":"+std::wstring(info->ProviderVersion,wcsnlen_s(info->ProviderVersion,256));
    return true;
}
    /// <summary>仅元数据查询本产品排除图标；冷文件暂缓原生Pin时菜单仍隐藏。失败保守隐藏，不读正文或联网。</summary>
    bool ExclusionVisible(const std::wstring& path) {
        ComPtr<IPropertyStore> source,memory;
        PROPERTYKEY key{};
        if(FAILED(SHGetPropertyStoreFromParsingName(path.c_str(),nullptr,GPS_DEFAULT,IID_PPV_ARGS(&source)))||
           FAILED(PSGetPropertyKeyFromName(L"System.StorageProviderCustomStates",&key)))return true;
        struct Value { PROPVARIANT Data{};~Value(){PropVariantClear(&Data);} } container,ids;
        if(FAILED(source->GetValue(key,&container.Data)))return true;
        if(container.Data.vt==VT_EMPTY)return false;
        if(container.Data.vt!=(VT_VECTOR|VT_UI1)||container.Data.caub.cElems==0||container.Data.caub.cElems>1048576||!container.Data.caub.pElems)return true;
        if(FAILED(PSCreateMemoryPropertyStore(IID_PPV_ARGS(&memory))))return true;
        ComPtr<IPersistSerializedPropStorage> persistence;
        if(FAILED(memory.As(&persistence))||FAILED(persistence->SetPropertyStorage(reinterpret_cast<PCUSERIALIZEDPROPSTORAGE>(container.Data.caub.pElems),container.Data.caub.cElems)))return true;
        const PROPERTYKEY idsKey={{0xfceff153,0xe839,0x4cf3,{0xa9,0xe7,0xea,0x22,0x83,0x20,0x94,0xb8}},121};
        if(FAILED(memory->GetValue(idsKey,&ids.Data)))return true;
        if(ids.Data.vt==VT_EMPTY)return false;
        if(ids.Data.vt!=(VT_VECTOR|VT_LPWSTR)||ids.Data.calpwstr.cElems>256||!ids.Data.calpwstr.pElems)return true;
        for(ULONG i=0;i<ids.Data.calpwstr.cElems;++i)if(ids.Data.calpwstr.pElems[i]&&wcscmp(ids.Data.calpwstr.pElems[i],L"2")==0)return true;
        return false;
    }
    /// <summary>只读属性句柄确认完整缓存；排除或无法确认的文件不暴露缓存动作，不读取正文。</summary>
    DWORD CacheActions(const std::wstring& path,hm::menu::Item& stamp,DWORD wanted) {
        auto raw=CreateFileW(path.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_BACKUP_SEMANTICS,nullptr);
        if(raw==INVALID_HANDLE_VALUE)return 0;
        struct Handle{HANDLE Value;~Handle(){CloseHandle(Value);}} handle{raw};
        FILE_STANDARD_INFO standard{};FILE_ATTRIBUTE_TAG_INFO tag{};
        if(!GetFileInformationByHandleEx(raw,FileStandardInfo,&standard,sizeof(standard))||!GetFileInformationByHandleEx(raw,FileIdInfo,&stamp.Identity,sizeof(stamp.Identity))||!GetFileInformationByHandleEx(raw,FileBasicInfo,&stamp.Basic,sizeof(stamp.Basic))||!GetFileInformationByHandleEx(raw,FileAttributeTagInfo,&tag,sizeof(tag)))return 0;
        if((tag.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)&&((tag.ReparseTag&~0xF000u)!=IO_REPARSE_TAG_CLOUD))return 0;
        stamp.Valid=true;stamp.Size=standard.EndOfFile.QuadPart;
        if(!wanted)return 0;
        // 先计算廉价候选，只有本次尚需确认的能力才读取Shell排除属性；普通文件不创建属性处理器。
        auto allow=[&](DWORD actions){return (actions&wanted)&&!ExclusionVisible(path)?actions:0u;};
        std::vector<BYTE> buffer(8192);DWORD returned=0;
        const auto status=CfGetPlaceholderInfo(raw,CF_PLACEHOLDER_INFO_STANDARD,buffer.data(),static_cast<DWORD>(buffer.size()),&returned);
        if(SUCCEEDED(status)){
            if(returned<offsetof(CF_PLACEHOLDER_STANDARD_INFO,FileIdentity))return 0;
            const auto info=reinterpret_cast<const CF_PLACEHOLDER_STANDARD_INFO*>(buffer.data());
            if(info->PinState==CF_PIN_STATE_EXCLUDED)return 0;
        }else if(!standard.Directory)return 0;
        // 目录可能同时含有完整和联机条目；仅显示命令，不在菜单查询中递归枚举。
        if(standard.Directory)return allow(3);
        if(standard.EndOfFile.QuadPart==0)return allow(1);
        std::vector<CF_FILE_RANGE> ranges(4096);returned=0;
        const auto rangeStatus=CfGetPlaceholderRangeInfo(raw,CF_PLACEHOLDER_RANGE_INFO_ONDISK,LARGE_INTEGER{},standard.EndOfFile,ranges.data(),static_cast<DWORD>(ranges.size()*sizeof(CF_FILE_RANGE)),&returned);
        if(FAILED(rangeStatus)||returned>ranges.size()*sizeof(CF_FILE_RANGE)||returned%sizeof(CF_FILE_RANGE))return 0;
        LONGLONG end=0;
        for(DWORD index=0;index<returned/sizeof(CF_FILE_RANGE);++index){const auto& range=ranges[index];if(range.StartingOffset.QuadPart>end)return allow(2);if(range.StartingOffset.QuadPart<0||range.Length.QuadPart<0||range.StartingOffset.QuadPart>MAXLONGLONG-range.Length.QuadPart)return 0;end=std::max(end,range.StartingOffset.QuadPart+range.Length.QuadPart);}
        return allow(end>=standard.EndOfFile.QuadPart?1:2);
    }
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
}

bool IsOwnedMountPath(const std::wstring& path,GUID& mount)noexcept{try{return OwnedRoot(path,mount);}catch(...){return false;}}
namespace hm::menu {
std::shared_ptr<const Summary> InspectSelection(IShellItemArray* selection,IUnknown* site,const std::atomic<bool>& canceled,Action action,bool invoke){
    const auto actionQuery=action==Action::FreeSpace||action==Action::KeepOnDevice;auto result=std::make_shared<Summary>();const auto start=GetTickCount64();DWORD count{};size_t characters{};std::set<std::wstring> paths;
    auto check=[&]{if(canceled.load()){result->Status=HRESULT_FROM_WIN32(ERROR_CANCELLED);return false;}if(GetTickCount64()-start>(invoke?120000u:5000u)){result->Status=HRESULT_FROM_WIN32(ERROR_TIMEOUT);return false;}return true;};
    auto inspect=[&](std::wstring path){
        if(!check()||path.empty()||path.size()>32767||(characters+=path.size())>256*1024)return false;
        GUID mount{};Item item;item.Path=std::move(path);if(!OwnedRoot(item.Path,mount,&item.RootRevision)||(!result->Items.empty()&&mount!=result->Mount)){result->Status=E_ACCESSDENIED;return false;}
        if(const auto delay=ProbeDelay())Sleep(delay);if(!check())return false;
        item.Actions=CacheActions(item.Path,item,!actionQuery?0u:invoke?(action==Action::FreeSpace?1u:2u):(3u&~result->Actions));if(!item.Valid){result->Status=HRESULT_FROM_WIN32(ERROR_FILE_INVALID);return false;}
        result->Mount=mount;result->Actions|=item.Actions;result->Items.push_back(std::move(item));++Counters[8];return true;
    };
    try{
        if(selection&&FAILED(selection->GetCount(&count))){result->Status=E_INVALIDARG;return result;}if(count>1024){result->Status=HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);return result;}
        if(!count){std::wstring path;if(FAILED(BackgroundPath(site,path))||!inspect(std::move(path))){if(result->Status==E_PENDING)result->Status=E_FAIL;return result;}}
        else for(DWORD index=0;index<count;++index){
            if(!check())return result;ComPtr<IShellItem> item;PWSTR raw{};
            if(FAILED(selection->GetItemAt(index,&item))||FAILED(item->GetDisplayName(SIGDN_FILESYSPATH,&raw))){result->Status=E_INVALIDARG;return result;}
            std::wstring path(raw);CoTaskMemFree(raw);auto key=path;std::transform(key.begin(),key.end(),key.begin(),[](wchar_t c){return static_cast<wchar_t>(towlower(c));});
            if(!paths.insert(key).second||!inspect(std::move(path))){if(result->Status==E_PENDING)result->Status=E_INVALIDARG;return result;}
            if((index+1)%16==0)SwitchToThread();
        }
        if(check()){result->Status=S_OK;result->CheckedActions=actionQuery?3u:0u;}
    }catch(...){result->Status=E_FAIL;}
    return result;
}
}

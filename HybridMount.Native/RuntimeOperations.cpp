#include "RuntimeOperations.h"
#include "CloudSession.h"
#include "CloudFileOperations.h"
#include "CloudProperties.h"
#include "LocalReadLeases.h"
#include "RootRegistryGuard.h"
#include "PreviewObserver.h"
#include <charconv>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.Provider.h>
#include <winrt/Windows.Security.Cryptography.h>
#include <shellapi.h>

namespace hm {
long long Number(const Json& value,const char* key){auto text=value.at(key).get<std::string>();long long result{};auto parsed=std::from_chars(text.data(),text.data()+text.size(),result);Require(parsed.ec==std::errc{}&&parsed.ptr==text.data()+text.size());return result;}
Json FileJson(const FileState& value){return {{"identity",value.Identity},{"usn",std::to_string(value.Usn)},{"length",std::to_string(value.Length)},{"lastWriteTime",std::to_string(value.LastWrite)},{"changeTime",std::to_string(value.Change)},{"isDirectory",value.Directory},{"reasons",value.Reasons},{"links",value.Links}};}
namespace {
std::mutex RootRegistryGate;
Json PlaceholderJson(const Placeholder& value){return {{"onDiskSize",std::to_string(value.OnDisk)},{"modifiedSize",std::to_string(value.Modified)},{"validatedSize",std::to_string(value.Validated)},{"propertiesSize",std::to_string(value.Properties)},{"fileId",std::to_string(value.FileId)},{"rootFileId",std::to_string(value.RootFileId)},{"pinState",value.Pin},{"inSyncState",value.InSync},{"identity",Base64(value.Identity.data(),static_cast<DWORD>(value.Identity.size()))}};}
Json Query(const Json& data){
    const auto path=FullPath(Text(data,"path"));auto file=OpenAttributes(path);auto state=InspectFile(file.Value);if(data.contains("expectedIdentity")&&!data.at("expectedIdentity").is_null())Require(state.Identity==data.at("expectedIdentity").get<std::string>(),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
    FILE_ATTRIBUTE_TAG_INFO tag{};WinCheck(GetFileInformationByHandleEx(file.Value,FileAttributeTagInfo,&tag,sizeof(tag)));Json result={{"stamp",FileJson(state)},{"attributes",tag.FileAttributes},{"tag",tag.ReparseTag},{"state",CfGetPlaceholderStateFromAttributeTag(tag.FileAttributes,tag.ReparseTag)},{"placeholder",nullptr},{"full",false}};
    const auto persisted=data.value("persisted",false);const auto cloud=(result.at("state").get<DWORD>()&CF_PLACEHOLDER_STATE_PLACEHOLDER)!=0;
    if(cloud||persisted){try{auto info=InspectPlaceholder(file.Value);result["placeholder"]=PlaceholderJson(info);if(data.value("ranges",false))result["full"]=HasFullContent(file.Value,state.Length);}catch(const Failure& error){if(cloud||error.Code!=HRESULT_FROM_WIN32(ERROR_NOT_A_CLOUD_FILE)&&error.Code!=HRESULT_FROM_WIN32(ERROR_CLOUD_FILE_NOT_IN_SYNC)&&error.Code!=E_INVALIDARG)throw;}}
    return result;
}
Json RootQuery(const Json& data){
    auto path=FullPath(Text(data,"path"));std::vector<BYTE> buffer(8192);DWORD length{};auto code=CfGetSyncRootInfoByPath(path.c_str(),CF_SYNC_ROOT_INFO_STANDARD,buffer.data(),static_cast<DWORD>(buffer.size()),&length);Json result={{"hresult",code}};if(FAILED(code))return result;
    Require(length>=offsetof(CF_SYNC_ROOT_STANDARD_INFO,SyncRootIdentity));auto info=reinterpret_cast<const CF_SYNC_ROOT_STANDARD_INFO*>(buffer.data());Require(info->SyncRootIdentityLength<=4096&&offsetof(CF_SYNC_ROOT_STANDARD_INFO,SyncRootIdentity)+info->SyncRootIdentityLength<=length);
    result["identity"]=Base64(info->SyncRootIdentity,info->SyncRootIdentityLength);result["fileId"]=std::to_string(info->SyncRootFileId.QuadPart);result["hydration"]=info->HydrationPolicy.Primary.us;result["modifier"]=info->HydrationPolicy.Modifier.us;result["population"]=info->PopulationPolicy.Primary.us;result["inSync"]=info->InSyncPolicy;result["hardlink"]=info->HardLinkPolicy;result["status"]=info->ProviderStatus;result["provider"]=Utf8(info->ProviderName);result["version"]=Utf8(info->ProviderVersion);return result;
}
/// <summary>只读快照在注册表并发收尾导致NO_MORE_ITEMS时有界重取；不重发Register或Unregister。</summary>
struct RegisteredRoot{winrt::Windows::Storage::Provider::StorageProviderSyncRootInfo Info;std::wstring Id,Path;};
std::vector<RegisteredRoot> RootSnapshot(){
    for(int attempt=0;;++attempt)try{std::vector<RegisteredRoot> result;for(auto item:winrt::Windows::Storage::Provider::StorageProviderSyncRootManager::GetCurrentSyncRoots()){auto folder=item.Path();result.push_back({item,std::wstring(item.Id()),folder?std::wstring(folder.Path()):std::wstring{}});}return result;}
    catch(const winrt::hresult_error& error){if(error.code()!=HRESULT_FROM_WIN32(ERROR_NO_MORE_ITEMS)||attempt>=4)throw Failure(error.code(),"root-enumeration");Sleep(10);}
}
std::wstring RootId(const Json& data){return ProductScope()+L"!"+CurrentSid()+L"!"+Wide(GuidN(data.at("mountId")));}
void GuardRoot(const Json& data,bool registered=true){
    auto path=FullPath(Text(data,"rootPath"));NoLinks(path,true);Require(InspectFile(path).Identity==data.at("rootIdentity").get<std::string>(),E_ACCESSDENIED);
    if(registered){auto marker=RootMarker(path);Require(GuidN(marker.at("MountId"))==GuidN(data.at("mountId")),E_ACCESSDENIED);}
}
/// <summary>两种进程身份共用原有根策略；更新定义时优先保留系统已有描述符。</summary>
winrt::Windows::Storage::Provider::StorageProviderSyncRootInfo Descriptor(const Json& data){
    using namespace winrt::Windows::Storage;using namespace winrt::Windows::Storage::Provider;
    auto path=FullPath(Text(data,"rootPath"));auto id=RootId(data);
    auto folder=StorageFolder::GetFolderFromPathAsync(path).get();StorageProviderSyncRootInfo info;
    info.Id(id);info.Path(folder);info.DisplayNameResource(Text(data,"name"));info.IconResource(ModuleDirectory()+L"\\HybridMount.NET.exe,0");info.HydrationPolicy(StorageProviderHydrationPolicy::Partial);info.PopulationPolicy(StorageProviderPopulationPolicy::AlwaysFull);info.HydrationPolicyModifier(StorageProviderHydrationPolicyModifier::None);
    info.InSyncPolicy(StorageProviderInSyncPolicy::FileLastWriteTime|StorageProviderInSyncPolicy::DirectoryLastWriteTime|StorageProviderInSyncPolicy::FileCreationTime|StorageProviderInSyncPolicy::DirectoryCreationTime);info.AllowPinning(true);info.HardlinkPolicy(StorageProviderHardlinkPolicy::None);info.Version(Text(data,"version"));info.ShowSiblingsAsGroup(false);
    auto marker=Json{{"Product",Utf8(ProductScope())},{"Schema",1},{"MountId",data.at("mountId")},{"Owner",Utf8(CurrentSid())}}.dump();std::vector<BYTE> bytes(marker.begin(),marker.end());info.Context(winrt::Windows::Security::Cryptography::CryptographicBuffer::CreateFromByteArray(bytes));
    for(auto pair:{std::pair{1,L"同步问题"},std::pair{2,L"上传排除"}}){StorageProviderItemPropertyDefinition property;property.Id(pair.first);property.DisplayNameResource(pair.second);info.StorageProviderItemPropertyDefinitions().Append(property);}
    return info;
}
/// <summary>所有本进程根枚举与修改串行，避免WinRT迭代器在其他挂载注销时失效。</summary>
Json OwnedRoot(const Json& data){
    using namespace winrt::Windows::Storage::Provider;GuardRoot(data,false);auto path=FullPath(Text(data,"rootPath"));auto native=RootQuery(Json{{"path",Utf8(path)}});auto code=native.at("hresult").get<HRESULT>();bool registered=SUCCEEDED(code),listed=false;
    if(registered)GuardRoot(data);else if(code!=static_cast<HRESULT>(0x80070186)&&code!=static_cast<HRESULT>(0x80070195))throw Failure(code,"root-native-query");
    auto under=[](const std::wstring& root,const std::wstring& full){return SamePath(root,full)||(full.size()>root.size()&&full[root.size()]==static_cast<wchar_t>(92)&&SamePath(root,full.substr(0,root.size())));};
    for(auto item:RootSnapshot()){if(item.Path.empty()){Require(item.Id!=RootId(data),E_FAIL);continue;}auto other=FullPath(item.Path);if(under(path,other)||under(other,path)){Require(item.Id==RootId(data)&&SamePath(path,other),HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS));listed=true;}}
    Require(!listed||registered,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));return {{"registered",registered}};
}
Json EnsureDefinition(const Json& data){
    using namespace winrt::Windows::Storage::Provider;GuardRoot(data);auto id=data.at("propertyId").get<int>();Require(id==1||id==2);StorageProviderSyncRootInfo info{nullptr};
    for(auto current:RootSnapshot())if(current.Id==RootId(data)){Require(SamePath(current.Path,FullPath(Text(data,"rootPath"))),E_ACCESSDENIED);info=current.Info;break;}
    if(!info)info=Descriptor(data);for(auto property:info.StorageProviderItemPropertyDefinitions())if(property.Id()==id)return Json::object();
    StorageProviderItemPropertyDefinition property;property.Id(id);property.DisplayNameResource(id==1?L"同步问题":L"上传排除");info.StorageProviderItemPropertyDefinitions().Append(property);GuardRoot(data);StorageProviderSyncRootManager::Register(info);GuardRoot(data);return Json::object();
}
Json RegisterRoot(const Json& data){
    using namespace winrt::Windows::Storage;using namespace winrt::Windows::Storage::Provider;
    GuardRoot(data,false);auto path=FullPath(Text(data,"rootPath"));auto id=RootId(data);bool existing=false;
    for(const auto& item:RootSnapshot()){
        if(item.Path.empty()){Require(item.Id!=id,E_FAIL);continue;}auto other=FullPath(item.Path);const auto under=[](const std::wstring& root,const std::wstring& full){return SamePath(root,full)||(full.size()>root.size()&&full[root.size()]==L'\\'&&SamePath(root,full.substr(0,root.size())));};
        if(under(path,other)||under(other,path)){Require(item.Id==id&&SamePath(path,other),HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS));existing=true;}
    }
    if(existing){GuardRoot(data);return {{"registered",true}};}
    {
        auto current=RootQuery(Json{{"path",Utf8(path)}});auto code=current.at("hresult").get<HRESULT>();
        if(SUCCEEDED(code)){GuardRoot(data);return {{"registered",true}};}
        Require(code==static_cast<HRESULT>(0x80070186)||code==static_cast<HRESULT>(0x80070195),code);
        Require(path.size()>3,E_ACCESSDENIED);auto drive=path.substr(0,3);Require(GetDriveTypeW(drive.c_str())!=DRIVE_REMOTE,E_ACCESSDENIED);wchar_t fs[64]{};WinCheck(GetVolumeInformationW(drive.c_str(),nullptr,0,nullptr,nullptr,nullptr,fs,64));Require(_wcsicmp(fs,L"NTFS")==0,E_ACCESSDENIED);
        auto file=OpenAttributes(path);Require(InspectFile(file.Value).Directory);WIN32_FIND_DATAW item{};auto search=FindFirstFileW((path+L"\\*").c_str(),&item);Require(search!=INVALID_HANDLE_VALUE,HRESULT_FROM_WIN32(GetLastError()));bool empty=true;do{if(wcscmp(item.cFileName,L".")&&wcscmp(item.cFileName,L"..")){empty=false;break;}}while(FindNextFileW(search,&item));auto findError=empty?GetLastError():ERROR_SUCCESS;FindClose(search);Require(!empty||findError==ERROR_NO_MORE_FILES,HRESULT_FROM_WIN32(findError));Require(empty,HRESULT_FROM_WIN32(ERROR_DIR_NOT_EMPTY));
    }
    auto info=Descriptor(data);
    GuardRoot(data,false);
    try{StorageProviderSyncRootManager::Register(info);}
    catch(const winrt::hresult_error& error){
        if(error.code()==HRESULT_FROM_WIN32(ERROR_NO_MORE_ITEMS)){
            auto current=RootQuery(Json{{"path",Utf8(path)}});if(current.at("hresult").get<HRESULT>()>=0){GuardRoot(data);for(auto existingRoot:RootSnapshot())if(existingRoot.Id==id&&SamePath(existingRoot.Path,path)&&existingRoot.Info.AllowPinning()&&existingRoot.Info.Version()==Text(data,"version")){bool issue=false,excluded=false;for(auto property:existingRoot.Info.StorageProviderItemPropertyDefinitions()){issue|=property.Id()==1;excluded|=property.Id()==2;}if(issue&&excluded)return {{"registered",true},{"reconciled",true}};}}
        }
        throw Failure(error.code(),"root-register");
    }
    GuardRoot(data);return {{"registered",true}};
}
}
void ValidateRuntimeRoot(const Json& data,bool registered){GuardRoot(data,registered);}
struct RuntimeOperations::Impl{RuntimeHost& Host;LocalReadLeases Reads;PreviewObserver Previews;std::mutex Gate;std::map<std::string,std::shared_ptr<CloudSession>> Sessions;explicit Impl(RuntimeHost& host):Host(host),Reads(host),Previews(host){}
    std::shared_ptr<CloudSession> Session(const Json& data){std::lock_guard lock(Gate);auto key=GuidN(data.at("sessionId"));auto found=Sessions.find(key);Require(found!=Sessions.end(),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));Require(GuidN(found->second->Root().at("mountId"))==GuidN(data.at("mountId")),E_ACCESSDENIED);return found->second;}
};
RuntimeOperations::RuntimeOperations(RuntimeHost& host):_impl(std::make_unique<Impl>(host)){}
RuntimeOperations::~RuntimeOperations()=default;
Json RuntimeOperations::Execute(RuntimeCommand& command){
    const auto apartment=CoInitializeEx(nullptr,COINIT_MULTITHREADED);struct Scope{HRESULT Value;~Scope(){if(SUCCEEDED(Value))CoUninitialize();}} scope{apartment};Check(apartment);command.CheckCancel();auto& data=command.Data;const auto& operation=command.Operation;
    std::unique_lock<std::mutex> rootLock(RootRegistryGate,std::defer_lock);std::unique_ptr<RootRegistryGuard> registryLease;if(operation=="ownedRoot"||operation=="registerRoot"||operation=="ensureDefinition"||operation=="unregisterRoot"||operation=="unregisterNativeRoot"||operation=="unregisterKnownRoot"||operation=="testResetDefinitions"){rootLock.lock();command.CheckCancel();registryLease=std::make_unique<RootRegistryGuard>(command.Cancel.Value);}
    // 活动根的每次领域命令必须绑定本次连接代次；只读无根查询及未连接根使用身份核验。
    if(operation!="connectRoot"&&data.contains("mountId")){
        if(data.contains("sessionId"))_impl->Session(data);
        else{std::lock_guard lock(_impl->Gate);for(auto& [_,session]:_impl->Sessions)Require(GuidN(session->Root().at("mountId"))!=GuidN(data.at("mountId")),E_ACCESSDENIED);}
    }
    if(operation=="connectRoot"){
        GuardRoot(data);auto session=std::make_shared<CloudSession>(_impl->Host,data);auto key=GuidN(data.at("sessionId"));
        {std::lock_guard lock(_impl->Gate);for(auto& [_,other]:_impl->Sessions)Require(GuidN(other->Root().at("mountId"))!=GuidN(data.at("mountId")),HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS));Require(_impl->Sessions.emplace(key,session).second);}
        try{session->Connect();}catch(...){std::lock_guard lock(_impl->Gate);_impl->Sessions.erase(key);throw;}return {{"sessionId",key}};
    }
    if(operation=="observePreviews"){auto session=_impl->Session(data);auto root=session->Root();GuardRoot(root);_impl->Previews.Configure(root,data.at("enabled").get<bool>());return Json::object();}
    if(operation=="disconnectRoot"){auto session=_impl->Session(data);_impl->Previews.Remove(GuidN(data.at("sessionId")));session->Stop();std::lock_guard lock(_impl->Gate);_impl->Sessions.erase(GuidN(data.at("sessionId")));return Json::object();}
    if(operation=="clearRoot"){_impl->Session(data)->BeginClear();return Json::object();}
    if(operation=="activity")return _impl->Session(data)->Activity(data.at("entryId"));
    if(operation=="blockEntry"){_impl->Session(data)->SetBlocked(data.at("entryId"),data.at("blocked"));return Json::object();}
    if(operation=="finishFetch"){_impl->Session(data)->Finish(GuidN(data.at("fetchId")),data.at("status").get<NTSTATUS>());return Json::object();}
    if(operation=="openLocalRead"||operation=="readLocalRange"||operation=="closeLocalRead")return _impl->Reads.Execute(command);
#ifdef HYBRIDMOUNT_NATIVE_TESTS
    if(operation=="testResetDefinitions"){GuardRoot(data);auto info=Descriptor(data);info.StorageProviderItemPropertyDefinitions().Clear();winrt::Windows::Storage::Provider::StorageProviderSyncRootManager::Register(info);return Json::object();}
    if(operation=="testSetCustomStates"){
        auto path=FullPath(Text(data,"path"));NoLinks(path,true);RootMarker(path);auto file=OpenAttributes(path);Require(InspectFile(file.Value).Identity==data.at("expectedIdentity").get<std::string>(),E_ACCESSDENIED);auto properties=winrt::single_threaded_vector<winrt::Windows::Storage::Provider::StorageProviderItemProperty>();Require(data.at("properties").size()<=256);
        for(auto item:data.at("properties")){auto id=item.at("id").get<int>();Require(id>=0&&Text(item,"value").size()<=32768&&Text(item,"icon").size()<=32768);winrt::Windows::Storage::Provider::StorageProviderItemProperty property;property.Id(id);property.Value(Text(item,"value"));property.IconResource(Text(item,"icon"));properties.Append(property);}
        RootRegistryGuard propertyGuard(command.Cancel.Value);auto item=winrt::Windows::Storage::StorageFile::GetFileFromPathAsync(path).get();winrt::Windows::Storage::Provider::StorageProviderItemProperties::SetAsync(item,properties).get();return Json::object();
    }
    if(operation=="testSetPin"||operation=="testDehydrate"){
        auto path=FullPath(Text(data,"path"));NoLinks(path,true);auto marker=RootMarker(path);GuidN(marker.at("MountId"));auto file=OpenAttributes(path,FILE_READ_ATTRIBUTES|FILE_WRITE_ATTRIBUTES);Require(InspectFile(file.Value).Identity==data.at("expectedIdentity").get<std::string>(),E_ACCESSDENIED);
        if(operation=="testSetPin"){auto pin=data.at("pin").get<CF_PIN_STATE>();Require(pin>=CF_PIN_STATE_UNSPECIFIED&&pin<=CF_PIN_STATE_INHERIT);Check(CfSetPinState(file.Value,pin,CF_SET_PIN_FLAG_NONE,nullptr));}
        else{HANDLE handle{};Check(CfOpenFileWithOplock(path.c_str(),CF_OPEN_FILE_FLAG_EXCLUSIVE|CF_OPEN_FILE_FLAG_WRITE_ACCESS,&handle));struct Close{HANDLE Value;~Close(){CfCloseHandle(Value);}} close{handle};LARGE_INTEGER offset{},length{};offset.QuadPart=Number(data,"offset");length.QuadPart=Number(data,"length");Check(CfDehydratePlaceholder(handle,offset,length,CF_DEHYDRATE_FLAG_NONE,nullptr));}return Json::object();
    }
#endif
    if(operation=="queryFile")return Query(data);
    if(operation=="readShellState")return ReadShellProperties(FullPath(Text(data,"path")));
    if(operation=="readCustomState")return ReadCustomProperties(FullPath(Text(data,"path")));
    if(operation=="queryRoot")return RootQuery(data);
    if(operation=="tagState")return {{"state",CfGetPlaceholderStateFromAttributeTag(data.at("attributes").get<DWORD>(),data.at("tag").get<DWORD>())}};
    if(operation=="ownedRoot")return OwnedRoot(data);
    if(operation=="registerRoot")return RegisterRoot(data);
    if(operation=="ensureDefinition")return EnsureDefinition(data);
    if(operation=="unregisterKnownRoot"){
        auto id=Text(data,"id"),prefix=ProductScope()+L"!"+CurrentSid()+L"!";Require(id.starts_with(prefix),E_ACCESSDENIED);auto mount=GuidN(Utf8(id.substr(prefix.size())));Require(id==prefix+Wide(mount),E_ACCESSDENIED);
        for(auto item:RootSnapshot())if(item.Id==id){auto path=FullPath(item.Path);auto guarded=Json{{"mountId",mount},{"rootPath",Utf8(path)},{"rootIdentity",InspectFile(path).Identity}};GuardRoot(guarded);winrt::Windows::Storage::Provider::StorageProviderSyncRootManager::Unregister(id);return Json::object();}throw Failure(HRESULT_FROM_WIN32(ERROR_NOT_FOUND));
    }
    if(operation=="unregisterRoot"){GuardRoot(data);winrt::Windows::Storage::Provider::StorageProviderSyncRootManager::Unregister(RootId(data));return Json::object();}
    if(operation=="unregisterNativeRoot"){GuardRoot(data);Check(CfUnregisterSyncRoot(Text(data,"rootPath").c_str()));return Json::object();}
    if(operation=="notify"){auto path=FullPath(Text(data,"path"));SHChangeNotify(SHCNE_UPDATEITEM,SHCNF_PATHW|SHCNF_FLUSHNOWAIT,path.c_str(),nullptr);return Json::object();}
    {
        std::shared_ptr<CloudSession> session;
        if(data.contains("sessionId"))session=_impl->Session(data);else if(data.contains("mountId")){std::lock_guard lock(_impl->Gate);for(auto& [_,candidate]:_impl->Sessions)if(GuidN(candidate->Root().at("mountId"))==GuidN(data.at("mountId"))){session=candidate;break;}}
        bool handled{};auto result=ExecuteCloudFile(_impl->Host,command,session.get(),handled);if(handled)return result;
    }
    if(operation=="ping")return {{"ready",true},{"scope",Utf8(ProductScope())},{"aumid",Utf8(ApplicationId())}};
    throw Failure(E_NOTIMPL);
}
void RuntimeOperations::Content(const RuntimeBlock& block){std::vector<std::shared_ptr<CloudSession>> sessions;{std::lock_guard lock(_impl->Gate);for(auto& [_,session]:_impl->Sessions)sessions.push_back(session);}for(auto& session:sessions)if(session->OwnsFetch(block.FetchId)){session->Transfer(block);return;}throw Failure(HRESULT_FROM_WIN32(ERROR_NOT_FOUND));}
void RuntimeOperations::Stop(){_impl->Previews.Stop();_impl->Reads.Stop();std::vector<std::shared_ptr<CloudSession>> sessions;{std::lock_guard lock(_impl->Gate);for(auto& [_,session]:_impl->Sessions)sessions.push_back(session);}for(auto& session:sessions)session->BeginClear();}
}

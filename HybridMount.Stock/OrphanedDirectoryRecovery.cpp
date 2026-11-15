#include "OrphanedDirectoryRecovery.h"
#include "NativeFile.h"
#include "RootRegistryGuard.h"
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.Provider.h>
#include <optional>
#include <functional>

namespace hm {
namespace {
constexpr int MaximumEntries=1000000;
/// <summary>孤立占位符被兼容层伪装为普通文件；仅本操作线程显式暴露云标签并恢复原模式。</summary>
struct ExposePlaceholders {
    using SetMode=char(NTAPI*)(char);SetMode Set;char Before;
    ExposePlaceholders(){
        Set=reinterpret_cast<SetMode>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"RtlSetThreadPlaceholderCompatibilityMode"));
        Require(Set!=nullptr,E_NOTIMPL);Before=Set(2);Require(Before>=0,E_FAIL);
    }
    ~ExposePlaceholders(){Set(Before);}
};
// Windows SDK旧目标宏未暴露此标志；Windows 11支持仅枚举磁盘条目的0x4语义。
constexpr DWORD OnDiskEntriesOnly=0x4;
bool CloudTag(DWORD tag){return (tag&0xffff0fff)==0x9000001a;}
/// <summary>Cloud Files可能隐藏属性查询的Tag；FSCTL只读持久重解析头，不读取正文。</summary>
FILE_ATTRIBUTE_TAG_INFO PersistentTag(HANDLE file){
    FILE_ATTRIBUTE_TAG_INFO tag{};WinCheck(GetFileInformationByHandleEx(file,FileAttributeTagInfo,&tag,sizeof(tag)));
    if(tag.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT){
        BYTE bytes[MAXIMUM_REPARSE_DATA_BUFFER_SIZE]{};DWORD returned{};
        WinCheck(DeviceIoControl(file,FSCTL_GET_REPARSE_POINT,nullptr,0,bytes,sizeof(bytes),&returned,nullptr));
        Require(returned>=8);memcpy(&tag.ReparseTag,bytes,sizeof(tag.ReparseTag));
    }
    return tag;
}
/// <summary>只接受明确的无同步根状态；权限、过滤器和其他错误保持失败。</summary>
bool MissingRoot(HRESULT code){return code==static_cast<HRESULT>(0x80070186)||code==static_cast<HRESULT>(0x80070195);}
struct Proof {LONGLONG FileId{};std::string Identity;};
std::optional<Proof> RootProof(const std::wstring& path){
    std::vector<BYTE> bytes(8192);DWORD size{};auto code=CfGetSyncRootInfoByPath(path.c_str(),CF_SYNC_ROOT_INFO_STANDARD,bytes.data(),static_cast<DWORD>(bytes.size()),&size);
    if(MissingRoot(code))return {};Check(code);Require(size>=offsetof(CF_SYNC_ROOT_STANDARD_INFO,SyncRootIdentity));
    auto info=reinterpret_cast<const CF_SYNC_ROOT_STANDARD_INFO*>(bytes.data());Require(info->SyncRootIdentityLength<=4096&&offsetof(CF_SYNC_ROOT_STANDARD_INFO,SyncRootIdentity)+info->SyncRootIdentityLength<=size);
    return Proof{info->SyncRootFileId.QuadPart,std::string(reinterpret_cast<const char*>(info->SyncRootIdentity),info->SyncRootIdentityLength)};
}
bool Under(const std::wstring& root,const std::wstring& path){return SamePath(root,path)||(path.size()>root.size()&&path[root.size()]==L'\\'&&SamePath(root,path.substr(0,root.size())));}
/// <summary>固定祖先及根目录身份；允许驱动属性更新，不共享删除，禁止根被重命名或替换。</summary>
struct RootLease {
    std::wstring Path;std::string Identity;LONGLONG FileId{};std::vector<Handle> Handles;
    explicit RootLease(const Json& data):Path(FullPath(Text(data,"path"))),Identity(data.at("rootIdentity").get<std::string>()){
        Require(Path.size()>3,E_ACCESSDENIED);auto volume=Path.substr(0,3);auto drive=GetDriveTypeW(volume.c_str());Require(drive==DRIVE_FIXED||drive==DRIVE_REMOVABLE,E_ACCESSDENIED);
        wchar_t fs[64]{};WinCheck(GetVolumeInformationW(volume.c_str(),nullptr,0,nullptr,nullptr,nullptr,fs,64));Require(_wcsicmp(fs,L"NTFS")==0,E_ACCESSDENIED);
        size_t position=3;
        while(true){
            auto end=Path.find(L'\\',position);auto current=end==std::wstring::npos?Path:Path.substr(0,end);
            auto raw=CreateFileW(current.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_OPEN_NO_RECALL,nullptr);
            if(raw==INVALID_HANDLE_VALUE)throw Failure(HRESULT_FROM_WIN32(GetLastError()),"orphan-inspect");Handles.emplace_back(raw);
            FILE_ATTRIBUTE_TAG_INFO tag{};WinCheck(GetFileInformationByHandleEx(raw,FileAttributeTagInfo,&tag,sizeof(tag)));
            if(!(tag.FileAttributes&FILE_ATTRIBUTE_DIRECTORY)||((tag.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)&&!CloudTag(tag.ReparseTag)))throw Failure(E_ACCESSDENIED,"orphan-ancestor-link");
            if(end==std::wstring::npos)break;position=end+1;
        }
        if(InspectFile(Handles.back().Value).Identity!=Identity)throw Failure(E_ACCESSDENIED,"orphan-root-identity");
        FILE_ID_INFO id{};WinCheck(GetFileInformationByHandleEx(Handles.back().Value,FileIdInfo,&id,sizeof(id)));memcpy(&FileId,id.FileId.Identifier,sizeof(FileId));
    }
    void Verify()const{NoLinks(Path,true);Require(InspectFile(Path).Identity==Identity,E_ACCESSDENIED);}
    /// <summary>枚举和删除期间禁止祖先及根属性被改写；注册连接之前与注销之前释放此加强租约。</summary>
    std::vector<Handle> Freeze()const{
        std::vector<Handle> handles;size_t position=3;
        while(true){
            auto end=Path.find(L'\\',position);auto current=end==std::wstring::npos?Path:Path.substr(0,end);
            auto raw=CreateFileW(current.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_OPEN_NO_RECALL,nullptr);
            if(raw==INVALID_HANDLE_VALUE)throw Failure(HRESULT_FROM_WIN32(GetLastError()));handles.emplace_back(raw);
            if(end==std::wstring::npos)break;position=end+1;
        }
        Verify();return handles;
    }
};
std::string Marker(const std::string& operation,const RootLease& root){
    return Json{{"Kind","HybridMount.OrphanedDirectoryRecovery"},{"Schema",1},{"Owner",Utf8(CurrentSid())},{"Operation",operation},{"RootIdentity",root.Identity}}.dump();
}
void VerifyProof(const Proof& proof,const RootLease& root,const std::string& marker){if(proof.FileId!=root.FileId||proof.Identity!=marker)throw Failure(E_ACCESSDENIED,"orphan-registration-identity");}
/// <summary>检查任何供应商的可见注册及两视图当前用户路径，拒绝重叠而非只过滤本产品。</summary>
void CheckRegisteredOverlap(const RootLease& root){
    using namespace winrt::Windows::Storage::Provider;
    for(auto item:StorageProviderSyncRootManager::GetCurrentSyncRoots()){
        auto folder=item.Path();Require(folder!=nullptr,E_FAIL);auto supplied=std::wstring(folder.Path());if(supplied.size()<3||supplied[1]!=L':')continue;auto path=FullPath(supplied);
        Require(!Under(root.Path,path)&&!Under(path,root.Path),HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS));
    }
    for(auto hive:{HKEY_CURRENT_USER,HKEY_LOCAL_MACHINE})for(auto view:{KEY_WOW64_64KEY,KEY_WOW64_32KEY}){
        HKEY raw{};auto code=RegOpenKeyExW(hive,L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\SyncRootManager",0,KEY_READ|view,&raw);
        if(code==ERROR_FILE_NOT_FOUND)continue;Require(code==ERROR_SUCCESS,HRESULT_FROM_WIN32(code));
        struct Key{HKEY Value;~Key(){RegCloseKey(Value);}} manager{raw};
        for(DWORD index=0;;++index){
            wchar_t name[16384]{};DWORD length=16384;code=RegEnumKeyExW(raw,index,name,&length,nullptr,nullptr,nullptr,nullptr);
            if(code==ERROR_NO_MORE_ITEMS)break;Require(code==ERROR_SUCCESS,HRESULT_FROM_WIN32(code));
            auto users=std::wstring(name)+L"\\UserSyncRoots";HKEY userRaw{};code=RegOpenKeyExW(raw,users.c_str(),0,KEY_QUERY_VALUE|view,&userRaw);
            if(code==ERROR_FILE_NOT_FOUND)continue;Require(code==ERROR_SUCCESS,HRESULT_FROM_WIN32(code));Key user{userRaw};
            wchar_t path[32768]{};DWORD bytes=sizeof(path);auto sid=CurrentSid();
            code=RegGetValueW(userRaw,nullptr,sid.c_str(),RRF_RT_REG_SZ|RRF_NOEXPAND,nullptr,path,&bytes);
            if(code==ERROR_FILE_NOT_FOUND)continue;Require(code==ERROR_SUCCESS,HRESULT_FROM_WIN32(code));
            Require(bytes>=sizeof(wchar_t)&&bytes<=sizeof(path)&&path[bytes/sizeof(wchar_t)-1]==0);
            if(wcslen(path)<3||path[1]!=L':')continue;auto other=FullPath(path);Require(!Under(root.Path,other)&&!Under(other,root.Path),HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS));
        }
    }
}
struct Search {HANDLE Value;~Search(){if(Value!=INVALID_HANDLE_VALUE)FindClose(Value);}};
/// <summary>只枚举已有磁盘条目；不请求云端目录填充，错误不当成空目录。</summary>
void Children(const std::wstring& path,const std::function<void(const std::wstring&,const WIN32_FIND_DATAW&)>& visit){
    WIN32_FIND_DATAW item{};Search search{FindFirstFileExW((path+L"\\*").c_str(),FindExInfoBasic,&item,FindExSearchNameMatch,nullptr,OnDiskEntriesOnly)};
    if(search.Value==INVALID_HANDLE_VALUE){auto code=GetLastError();if(code==ERROR_FILE_NOT_FOUND)return;throw Failure(HRESULT_FROM_WIN32(code),"orphan-inspect");}
    do{if(wcscmp(item.cFileName,L".")&&wcscmp(item.cFileName,L".."))visit(path+L"\\"+item.cFileName,item);}while(FindNextFileW(search.Value,&item));
    auto code=GetLastError();Require(code==ERROR_NO_MORE_FILES,HRESULT_FROM_WIN32(code));
}
bool Empty(const std::wstring& path){bool empty=true;Children(path,[&](const auto&,const auto&){empty=false;});return empty;}
struct Scan {int Entries{},Cloud{};bool Registered{};};
Scan Inspect(RuntimeCommand& command,const RootLease& root,const std::string& marker,bool resume){
    auto frozen=root.Freeze();CheckRegisteredOverlap(root);auto proof=RootProof(root.Path);if(proof){Require(resume,E_ACCESSDENIED);VerifyProof(*proof,root,marker);}
    Scan result;result.Registered=proof.has_value();
    std::function<void(const std::wstring&,int)> walk=[&](const std::wstring& path,int depth){
        command.CheckCancel();Require(depth<=512,HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW));
        auto raw=CreateFileW(path.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_OPEN_NO_RECALL,nullptr);
        if(raw==INVALID_HANDLE_VALUE)throw Failure(HRESULT_FROM_WIN32(GetLastError()),"orphan-inspect");Handle directory(raw);
        auto ownTag=PersistentTag(raw);Require(!(ownTag.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)||CloudTag(ownTag.ReparseTag),E_ACCESSDENIED);
        if(auto native=RootProof(path)){Require(resume,E_ACCESSDENIED);VerifyProof(*native,root,marker);}
        Children(path,[&](const auto& child,const auto& item){
            command.CheckCancel();Require(++result.Entries<=MaximumEntries,HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW));
            bool cloud=false;
            if(item.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT){auto file=OpenAttributes(child);cloud=CloudTag(PersistentTag(file.Value).ReparseTag);}
            if(cloud)++result.Cloud;
            if((item.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)&&(!(item.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)||cloud))walk(child,depth+1);
        });
    };
    walk(root.Path,0);
    Require(resume||result.Cloud>0,HRESULT_FROM_WIN32(ERROR_NOT_A_CLOUD_FILE));return result;
}
/// <summary>临时原生注册仅属于本操作；析构再次尽力收尾，绝不注销其他身份的根。</summary>
struct TemporaryRoot {
    const RootLease& Root;std::string Identity;CF_CONNECTION_KEY Connection{};bool Connected{};
    TemporaryRoot(const RootLease& root,std::string marker):Root(root),Identity(std::move(marker)){}
    HRESULT Close()noexcept{
        HRESULT failure=S_OK;
        if(Connected){auto code=CfDisconnectSyncRoot(Connection);if(SUCCEEDED(code))Connected=false;else failure=code;}
        try{
            Root.Verify();if(auto proof=RootProof(Root.Path)){VerifyProof(*proof,Root,Identity);auto code=CfUnregisterSyncRoot(Root.Path.c_str());if(FAILED(code)&&SUCCEEDED(failure))failure=code;}
            if(RootProof(Root.Path)&&SUCCEEDED(failure))failure=E_FAIL;
        }catch(const Failure& error){if(SUCCEEDED(failure))failure=error.Code;}catch(...){if(SUCCEEDED(failure))failure=E_FAIL;}
        return failure;
    }
    ~TemporaryRoot(){Close();}
    void Register(){
        Root.Verify();if(auto proof=RootProof(Root.Path)){VerifyProof(*proof,Root,Identity);return;}
        CF_SYNC_REGISTRATION registration{};registration.StructSize=sizeof(registration);registration.ProviderName=L"HybridMount local recovery";registration.ProviderVersion=L"1";
        registration.SyncRootIdentity=Identity.data();registration.SyncRootIdentityLength=static_cast<DWORD>(Identity.size());
        registration.ProviderId={0xcb0b0f60,0xc748,0x4cdb,{0x82,0xb8,0x8c,0x69,0xe6,0x19,0x12,0x60}};
        CF_SYNC_POLICIES policies{};policies.StructSize=sizeof(policies);policies.Hydration.Primary=CF_HYDRATION_POLICY_ALWAYS_FULL;policies.Population.Primary=CF_POPULATION_POLICY_ALWAYS_FULL;
        auto code=CfRegisterSyncRoot(Root.Path.c_str(),&registration,&policies,CF_REGISTER_FLAG_NONE);if(FAILED(code))throw Failure(code,"orphan-register");
        auto proof=RootProof(Root.Path);Require(proof.has_value());VerifyProof(*proof,Root,Identity);
    }
    void Connect(){
        CF_CALLBACK_REGISTRATION callbacks[]{CF_CALLBACK_REGISTRATION_END};
        auto code=CfConnectSyncRoot(Root.Path.c_str(),callbacks,nullptr,CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION,&Connection);
        if(FAILED(code))throw Failure(code,"orphan-connect");Connected=true;
    }
};
}
Json ExecuteOrphanedDirectory(RuntimeHost& host,RuntimeCommand& command){
    ExposePlaceholders compatibility;RootRegistryGuard registry(command.Cancel.Value);RootLease root(command.Data);const auto operation=GuidN(command.Data.at("operationId"));auto marker=Marker(operation,root);
    const auto resume=command.Data.value("resume",false);auto scan=Inspect(command,root,marker,resume);
    if(command.Operation=="inspectOrphanedDirectory")return {{"entries",scan.Entries},{"cloud",scan.Cloud},{"registered",scan.Registered}};
    Require(command.Operation=="clearOrphanedDirectory",E_NOTIMPL);TemporaryRoot temporary(root,marker);
    int deleted=command.Data.value("deleted",0),failures=0;Require(deleted>=0&&deleted<=INT_MAX-MaximumEntries);Json errors=Json::array();std::string stage="Confirmed",failedStage;bool contentComplete=command.Data.value("contentCompleted",false);HRESULT failure=S_OK;
    std::vector<Handle> frozen;
    auto checkpoint=[&](const std::string& value,bool safety=false,bool irreversible=false){
        stage=value;host.Checkpoint(command,"orphanStage",{{"stage",stage},{"deleted",deleted},{"failures",failures},{"contentCompleted",contentComplete},{"hresult",failure},{"failedStage",failedStage}},irreversible,safety);
    };
    try{
        if(contentComplete){Require(Empty(root.Path),HRESULT_FROM_WIN32(ERROR_DIR_NOT_EMPTY));}
        else{
            checkpoint("Registering");temporary.Register();checkpoint("Registered");temporary.Connect();checkpoint("Connected");frozen=root.Freeze();checkpoint("Clearing");
            auto last=GetTickCount64();int visited=0;
            std::function<void(const std::wstring&,int)> clear=[&](const std::wstring& directory,int depth){
                Require(depth<=512,HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW));command.CheckCancel();root.Verify();
                auto proof=RootProof(directory);Require(proof.has_value(),E_ACCESSDENIED);VerifyProof(*proof,root,marker);
                Children(directory,[&](const auto& path,const auto&){
                    command.CheckCancel();Require(++visited<=MaximumEntries,HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW));
                    try{
                        auto raw=CreateFileW(path.c_str(),FILE_READ_ATTRIBUTES|DELETE,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_NO_RECALL,nullptr);
                        if(raw==INVALID_HANDLE_VALUE)throw Failure(HRESULT_FROM_WIN32(GetLastError()),"orphan-clear");Handle file(raw);
                        auto tag=PersistentTag(raw);
                        const auto cloud=CloudTag(tag.ReparseTag);
                        if((tag.FileAttributes&FILE_ATTRIBUTE_DIRECTORY)&&(!(tag.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)||cloud))clear(path,depth+1);
                        command.CheckCancel();FILE_DISPOSITION_INFO disposition{TRUE};WinCheck(SetFileInformationByHandle(raw,FileDispositionInfo,&disposition,sizeof(disposition)));++deleted;
                    }catch(const Failure& error){
                        if(error.Code==HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED))throw;++failures;if(SUCCEEDED(failure))failure=error.Code;
                        if(errors.size()<16){auto relative=Utf8(path.substr(root.Path.size()+1));if(relative.size()>2048)relative=Utf8(path.substr(path.find_last_of(L'\\')+1));errors.push_back(Json{{"path",relative},{"hresult",error.Code}});}
                    }
                    if(GetTickCount64()-last>=250){checkpoint("Clearing");last=GetTickCount64();}
                });
            };
            clear(root.Path,0);Require(failures==0,failure);Require(Empty(root.Path),HRESULT_FROM_WIN32(ERROR_DIR_NOT_EMPTY));contentComplete=true;checkpoint("ContentComplete",false,true);
        }
    }catch(const Failure& error){failure=error.Code;}catch(const winrt::hresult_error& error){failure=error.code();}catch(...){failure=E_FAIL;}
    // 取消后的收尾使用安全检查点；即使日志ACK失败，也先尝试断开并注销自己的临时注册。
    if(FAILED(failure))failedStage=stage;
    frozen.clear();auto cleanup=temporary.Close();if(FAILED(cleanup)&&SUCCEEDED(failure)){failure=cleanup;failedStage="Cleanup";}
    if(contentComplete&&SUCCEEDED(failure))try{root.Verify();Require(Empty(root.Path),HRESULT_FROM_WIN32(ERROR_DIR_NOT_EMPTY));}catch(const Failure& error){failure=error.Code;failedStage="ContentChanged";}
    const auto completed=contentComplete&&SUCCEEDED(failure)&&SUCCEEDED(cleanup);
    try{checkpoint(completed?"Completed":FAILED(cleanup)?"CleanupPending":command.Canceled()?"Canceled":"Failed",true,completed);}catch(...){if(SUCCEEDED(failure))failure=HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE);}
    return {{"completed",completed&&SUCCEEDED(failure)},{"deleted",deleted},{"stage",stage},{"hresult",failure},{"cleanupHresult",cleanup},{"errors",errors},{"failures",failures},{"contentCompleted",contentComplete},{"failedStage",failedStage}};
}
}

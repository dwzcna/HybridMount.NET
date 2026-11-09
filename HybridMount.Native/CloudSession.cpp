#include "CloudSession.h"
#include <shlwapi.h>

namespace hm {
namespace {
constexpr NTSTATUS CanceledStatus=static_cast<NTSTATUS>(0xc000cf16),UnavailableStatus=static_cast<NTSTATUS>(0xc000cf00);
std::string NewId(){GUID id{};Check(CoCreateGuid(&id));wchar_t text[40]{};Require(StringFromGUID2(id,text,40)>0);return GuidN(Utf8(text));}
bool CanceledCompletion(HRESULT code){return code==static_cast<HRESULT>(0x8007017c)||code==static_cast<HRESULT>(0x80070189)||code==HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED);}
CF_OPERATION_INFO Operation(const CF_CALLBACK_INFO& info,CF_OPERATION_TYPE type){CF_OPERATION_INFO value{};value.StructSize=sizeof(value);value.Type=type;value.ConnectionKey=info.ConnectionKey;value.TransferKey=info.TransferKey;value.RequestKey=info.RequestKey;return value;}
HRESULT Complete(const CF_OPERATION_INFO& info,long long offset,long long length,NTSTATUS status,const void* buffer=nullptr){CF_OPERATION_PARAMETERS parameters{};parameters.ParamSize=static_cast<ULONG>(offsetof(CF_OPERATION_PARAMETERS,TransferData)+sizeof(parameters.TransferData));parameters.TransferData.CompletionStatus=status;parameters.TransferData.Offset.QuadPart=offset;parameters.TransferData.Length.QuadPart=length;parameters.TransferData.Buffer=buffer;return CfExecute(&info,&parameters);}
void Ack(const CF_CALLBACK_INFO& info,CF_CALLBACK_TYPE type,NTSTATUS status){auto operation=Operation(info,type==CF_CALLBACK_TYPE_NOTIFY_DELETE?CF_OPERATION_TYPE_ACK_DELETE:CF_OPERATION_TYPE_ACK_RENAME);CF_OPERATION_PARAMETERS parameters{};if(type==CF_CALLBACK_TYPE_NOTIFY_DELETE){parameters.ParamSize=static_cast<ULONG>(offsetof(CF_OPERATION_PARAMETERS,AckDelete)+sizeof(parameters.AckDelete));parameters.AckDelete.CompletionStatus=status;}else{parameters.ParamSize=static_cast<ULONG>(offsetof(CF_OPERATION_PARAMETERS,AckRename)+sizeof(parameters.AckRename));parameters.AckRename.CompletionStatus=status;}Check(CfExecute(&operation,&parameters));}
std::wstring CallbackPath(const Json& root,PCWSTR raw){Require(raw!=nullptr);std::wstring path(raw);Require(path.size()<=32767);if(path.size()>1&&path.front()==L'\\'&&path[1]!=L'\\')path=Text(root,"rootPath").substr(0,2)+path;return FullPath(path);}
}
struct CloudSession::Impl {
    RuntimeHost& Host;Json Root;std::string Mount,Session;CF_CONNECTION_KEY Connection{};mutable std::mutex Gate;bool Clearing{},Stopped{};
    struct Fetch {CF_OPERATION_INFO Operation{};std::string Id,Entry;long long Offset{},Length{},Start{},End{},Next{};bool Canceled{},Done{};std::mutex Gate;};
    std::map<std::string,std::shared_ptr<Fetch>> Fetches;std::map<std::string,int> Opens;std::set<std::string> Blocked;std::map<std::string,ULONGLONG> LastReads;
    explicit Impl(RuntimeHost& host,const Json& root):Host(host),Root(root),Mount(GuidN(root.at("mountId"))),Session(GuidN(root.at("sessionId"))),Clearing(root.value("blockHydration",false)){}
    bool Event(const char* name,Json data){return Host.Send({{"kind","event"},{"mountId",Mount},{"sessionId",Session},{"event",name},{"data",std::move(data)}});}
    std::string Entry(const CF_CALLBACK_INFO& info){Require(info.FileIdentity&&info.FileIdentityLength>0&&info.FileIdentityLength<=4096);auto marker=Parse(std::string(static_cast<const char*>(info.FileIdentity),info.FileIdentityLength),4096);Require(GuidN(marker.at("MountId"))==Mount,E_ACCESSDENIED);auto id=marker.at("EntryId").get<std::string>();Require(!id.empty()&&id.size()<=4096);return id;}
    template<CF_CALLBACK_TYPE Type> static void CALLBACK Callback(const CF_CALLBACK_INFO* info,const CF_CALLBACK_PARAMETERS* parameters)noexcept{
        if(!info||!parameters||!info->CallbackContext)return;auto self=static_cast<Impl*>(const_cast<void*>(info->CallbackContext));
        try{self->Dispatch(Type,*info,*parameters);}catch(...){try{if constexpr(Type==CF_CALLBACK_TYPE_FETCH_DATA)Complete(Operation(*info,CF_OPERATION_TYPE_TRANSFER_DATA),parameters->FetchData.RequiredFileOffset.QuadPart,parameters->FetchData.RequiredLength.QuadPart,UnavailableStatus);if constexpr(Type==CF_CALLBACK_TYPE_NOTIFY_DELETE||Type==CF_CALLBACK_TYPE_NOTIFY_RENAME)Ack(*info,Type,static_cast<NTSTATUS>(0xc0000022));self->Event("callbackFailure",{{"type",Type},{"hresult",E_FAIL}});}catch(...){}}
    }
    void Dispatch(CF_CALLBACK_TYPE type,const CF_CALLBACK_INFO& info,const CF_CALLBACK_PARAMETERS& parameters){
        auto entry=Entry(info);
        if(type==CF_CALLBACK_TYPE_FETCH_DATA){
            auto fetch=std::make_shared<Fetch>();fetch->Operation=Operation(info,CF_OPERATION_TYPE_TRANSFER_DATA);fetch->Id=NewId();fetch->Entry=entry;fetch->Offset=parameters.FetchData.RequiredFileOffset.QuadPart;fetch->Length=parameters.FetchData.RequiredLength.QuadPart;Require(fetch->Offset>=0&&fetch->Length>0&&info.FileSize.QuadPart>=fetch->Offset&&fetch->Length<=info.FileSize.QuadPart-fetch->Offset);
            fetch->Start=fetch->Offset/4096*4096;const auto end=fetch->Offset+fetch->Length;fetch->End=end>LLONG_MAX-4095?info.FileSize.QuadPart:std::min(info.FileSize.QuadPart,(end+4095)/4096*4096);fetch->Next=fetch->Start;
            {std::lock_guard lock(Gate);if(Clearing||Stopped||Blocked.contains(entry)){Complete(fetch->Operation,fetch->Offset,fetch->Length,CanceledStatus);return;}Fetches.emplace(fetch->Id,fetch);LastReads[entry]=GetTickCount64();}
            const auto pid=info.ProcessInfo?info.ProcessInfo->ProcessId:0;std::wstring image=info.ProcessInfo&&info.ProcessInfo->ImagePath?PathFindFileNameW(info.ProcessInfo->ImagePath):L"unknown";if(image.size()>260)image=L"unknown";
            if(!Event("fetch",{{"fetchId",fetch->Id},{"entryId",entry},{"offset",std::to_string(fetch->Offset)},{"length",std::to_string(fetch->Length)},{"priority",info.PriorityHint},{"explicit",(parameters.FetchData.Flags&CF_CALLBACK_FETCH_DATA_FLAG_EXPLICIT_HYDRATION)!=0},{"processId",pid},{"image",Utf8(image)}})){
                {std::lock_guard lock(Gate);Fetches.erase(fetch->Id);}Complete(fetch->Operation,fetch->Offset,fetch->Length,UnavailableStatus);
            }return;
        }
        if(type==CF_CALLBACK_TYPE_CANCEL_FETCH_DATA){
            std::vector<std::shared_ptr<Fetch>> canceled;{std::lock_guard lock(Gate);for(auto& [_,fetch]:Fetches){auto offset=parameters.Cancel.FetchData.FileOffset.QuadPart,length=parameters.Cancel.FetchData.Length.QuadPart;bool covered=offset>=0&&offset<=fetch->Offset&&(length==-1||(length>0&&fetch->Offset-offset<=length&&fetch->Length<=length-(fetch->Offset-offset)));if(fetch->Operation.TransferKey.QuadPart==info.TransferKey.QuadPart&&fetch->Operation.RequestKey.QuadPart==info.RequestKey.QuadPart&&covered)canceled.push_back(fetch);}}
            for(auto& fetch:canceled){{std::lock_guard lock(fetch->Gate);fetch->Canceled=true;}Event("cancelFetch",{{"fetchId",fetch->Id}});}return;
        }
        const auto self=info.ProcessInfo&&(info.ProcessInfo->ProcessId==GetCurrentProcessId()||info.ProcessInfo->ProcessId==Host.ParentProcessId());
        if(type==CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION||type==CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION){if(self)return;{std::lock_guard lock(Gate);auto& count=Opens[entry];count=type==CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION?count+1:std::max(0,count-1);}Event(type==CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION?"open":"close",{{"entryId",entry}});return;}
        if(type==CF_CALLBACK_TYPE_NOTIFY_DELETE){Ack(info,type,0);return;}
        if(type==CF_CALLBACK_TYPE_NOTIFY_RENAME){
            const char* reason="sessionStopping";
            try{
                {std::lock_guard lock(Gate);Require(!Clearing&&!Stopped,E_ACCESSDENIED);}
                reason="outOfRoot";Require((parameters.Rename.Flags&CF_CALLBACK_RENAME_FLAG_TARGET_IN_SCOPE)!=0,E_ACCESSDENIED);
                reason="invalidTarget";auto target=CallbackPath(Root,parameters.Rename.TargetPath);auto root=FullPath(Text(Root,"rootPath"));
                reason="outOfRoot";Require(target.size()>root.size()&&target[root.size()]==L'\\'&&SamePath(target.substr(0,root.size()),root),E_ACCESSDENIED);
                reason="unsafeTarget";NoLinks(target.substr(0,target.find_last_of(L'\\')),true);auto marker=RootMarker(target.substr(0,target.find_last_of(L'\\')));
                reason="differentMount";Require(GuidN(marker.at("MountId"))==Mount,E_ACCESSDENIED);
            }catch(...){try{Host.Send({{"kind","event"},{"mountId",Mount},{"sessionId",Session},{"event","renameRejected"},{"data",{{"entryId",entry},{"sourcePath",Utf8(info.NormalizedPath?info.NormalizedPath:L"")},{"targetPath",Utf8(parameters.Rename.TargetPath?parameters.Rename.TargetPath:L"")},{"reason",reason}}}},false);}catch(...){}throw;}
            Ack(info,type,0);return;
        }
        if(type==CF_CALLBACK_TYPE_NOTIFY_DELETE_COMPLETION){Event("deleted",{{"entryId",entry}});return;}
        if(type==CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION){Event("renamed",{{"entryId",entry},{"path",Utf8(CallbackPath(Root,info.NormalizedPath))},{"oldPath",Utf8(CallbackPath(Root,parameters.RenameCompletion.SourcePath))}});}
    }
};
CloudSession::CloudSession(RuntimeHost& host,const Json& root):_impl(std::make_unique<Impl>(host,root)){}
CloudSession::~CloudSession(){try{Stop();}catch(...){}}
const Json& CloudSession::Root()const{return _impl->Root;}
void CloudSession::Connect(){auto& p=*_impl;CF_CALLBACK_REGISTRATION callbacks[]{
    {CF_CALLBACK_TYPE_FETCH_DATA,Impl::Callback<CF_CALLBACK_TYPE_FETCH_DATA>},{CF_CALLBACK_TYPE_CANCEL_FETCH_DATA,Impl::Callback<CF_CALLBACK_TYPE_CANCEL_FETCH_DATA>},
    {CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION>},{CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION>},
    {CF_CALLBACK_TYPE_NOTIFY_DELETE,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_DELETE>},{CF_CALLBACK_TYPE_NOTIFY_DELETE_COMPLETION,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_DELETE_COMPLETION>},
    {CF_CALLBACK_TYPE_NOTIFY_RENAME,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_RENAME>},{CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION>},CF_CALLBACK_REGISTRATION_END};
    Check(CfConnectSyncRoot(Text(p.Root,"rootPath").c_str(),callbacks,&p,CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO|CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,&p.Connection));
}
bool CloudSession::Active(const std::string& entry)const{auto& p=*_impl;std::lock_guard lock(p.Gate);if(p.Opens.contains(entry)&&p.Opens.at(entry)>0)return true;for(auto& [_,fetch]:p.Fetches)if(fetch->Entry==entry)return true;return false;}
Json CloudSession::Activity(const std::string& entry)const{return {{"active",Active(entry)}};}
void CloudSession::SetBlocked(const std::string& entry,bool blocked){auto& p=*_impl;std::lock_guard lock(p.Gate);if(blocked)p.Blocked.insert(entry);else p.Blocked.erase(entry);}
bool CloudSession::OwnsFetch(const std::string& id)const{std::lock_guard lock(_impl->Gate);return _impl->Fetches.contains(id);}
void CloudSession::Transfer(const RuntimeBlock& block){
    std::shared_ptr<Impl::Fetch> fetch;{std::lock_guard lock(_impl->Gate);auto found=_impl->Fetches.find(block.FetchId);Require(found!=_impl->Fetches.end(),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));fetch=found->second;}
    std::lock_guard lock(fetch->Gate);Require(!fetch->Canceled&&!fetch->Done,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));Require(block.Offset==fetch->Next&&block.Offset>=fetch->Start&&block.Offset<=fetch->End&&static_cast<long long>(block.Bytes.size())<=fetch->End-block.Offset);
    Check(Complete(fetch->Operation,block.Offset,static_cast<long long>(block.Bytes.size()),0,block.Bytes.data()));fetch->Next+=block.Bytes.size();
}
void CloudSession::Finish(const std::string& id,NTSTATUS status){
    std::shared_ptr<Impl::Fetch> fetch;{std::lock_guard lock(_impl->Gate);auto found=_impl->Fetches.find(id);if(found==_impl->Fetches.end())return;fetch=found->second;_impl->Fetches.erase(found);}
    std::lock_guard lock(fetch->Gate);if(fetch->Done)return;fetch->Done=true;if(status==0){if(!fetch->Canceled&&fetch->Next>=fetch->Offset+fetch->Length)return;status=UnavailableStatus;}
    auto code=Complete(fetch->Operation,fetch->Offset,fetch->Length,status);if(fetch->Canceled&&CanceledCompletion(code))return;Check(code);
}
void CloudSession::BeginClear(){auto& p=*_impl;std::vector<std::string> ids;{std::lock_guard lock(p.Gate);p.Clearing=true;for(auto& [id,fetch]:p.Fetches){ids.push_back(id);}}
    for(auto& id:ids){{std::shared_ptr<Impl::Fetch> fetch;{std::lock_guard lock(p.Gate);auto found=p.Fetches.find(id);if(found!=p.Fetches.end())fetch=found->second;}if(fetch){std::lock_guard lock(fetch->Gate);fetch->Canceled=true;}}p.Event("cancelFetch",{{"fetchId",id}});try{Finish(id,CanceledStatus);}catch(...){}}
}
void CloudSession::Stop(){auto& p=*_impl;{std::lock_guard lock(p.Gate);if(p.Stopped)return;p.Stopped=true;}BeginClear();if(p.Connection.Internal){auto key=p.Connection;p.Connection={};Check(CfDisconnectSyncRoot(key));}}
}

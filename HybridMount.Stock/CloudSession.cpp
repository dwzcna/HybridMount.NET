#include "CloudSession.h"
#include <shlwapi.h>

namespace hm {
namespace {
// 重入时继承更早截止时间，取消区间中的多次通知共用同一45ms排队预算。
thread_local ULONGLONG CallbackDeadline{};
struct CallbackBudget{ULONGLONG Previous=CallbackDeadline;CallbackBudget(){auto end=GetTickCount64()+45;CallbackDeadline=Previous?std::min(Previous,end):end;}~CallbackBudget(){CallbackDeadline=Previous;}};
constexpr NTSTATUS CanceledStatus=static_cast<NTSTATUS>(0xc000cf16),UnavailableStatus=static_cast<NTSTATUS>(0xc000cf00);
std::string NewId(){GUID id{};Check(CoCreateGuid(&id));wchar_t text[40]{};Require(StringFromGUID2(id,text,40)>0);return GuidN(Utf8(text));}
bool CanceledCompletion(HRESULT code){return code==static_cast<HRESULT>(0x8007017c)||code==static_cast<HRESULT>(0x80070189)||code==HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED);}
CF_OPERATION_INFO Operation(const CF_CALLBACK_INFO& info,CF_OPERATION_TYPE type){CF_OPERATION_INFO value{};value.StructSize=sizeof(value);value.Type=type;value.ConnectionKey=info.ConnectionKey;value.TransferKey=info.TransferKey;value.RequestKey=info.RequestKey;return value;}
HRESULT Complete(const CF_OPERATION_INFO& info,long long offset,long long length,NTSTATUS status,const void* buffer=nullptr){CF_OPERATION_PARAMETERS parameters{};parameters.ParamSize=static_cast<ULONG>(offsetof(CF_OPERATION_PARAMETERS,TransferData)+sizeof(parameters.TransferData));parameters.TransferData.CompletionStatus=status;parameters.TransferData.Offset.QuadPart=offset;parameters.TransferData.Length.QuadPart=length;parameters.TransferData.Buffer=buffer;return CfExecute(&info,&parameters);}
void Ack(const CF_CALLBACK_INFO& info,CF_CALLBACK_TYPE type,NTSTATUS status){auto operation=Operation(info,type==CF_CALLBACK_TYPE_NOTIFY_DELETE?CF_OPERATION_TYPE_ACK_DELETE:CF_OPERATION_TYPE_ACK_RENAME);CF_OPERATION_PARAMETERS parameters{};if(type==CF_CALLBACK_TYPE_NOTIFY_DELETE){parameters.ParamSize=static_cast<ULONG>(offsetof(CF_OPERATION_PARAMETERS,AckDelete)+sizeof(parameters.AckDelete));parameters.AckDelete.CompletionStatus=status;}else{parameters.ParamSize=static_cast<ULONG>(offsetof(CF_OPERATION_PARAMETERS,AckRename)+sizeof(parameters.AckRename));parameters.AckRename.CompletionStatus=status;}Check(CfExecute(&operation,&parameters));}
std::wstring CallbackPath(const Json& root,PCWSTR raw){Require(raw!=nullptr);std::wstring path(raw);Require(path.size()<=32767);if(path.size()>1&&path.front()==L'\\'&&path[1]!=L'\\')path=Text(root,"rootPath").substr(0,2)+path;return FullPath(path);}
}
struct CloudSession::Impl {
    RuntimeHost& Host;Json Root;std::string Mount,Session;CF_CONNECTION_KEY Connection{};mutable std::mutex Gate;std::mutex ConnectionGate;std::mutex CallbackGate;std::condition_variable CallbackWake;size_t Callbacks{};bool Clearing{},Stopped{},Detached{};
#ifdef HYBRIDMOUNT_NATIVE_TESTS
    std::atomic<int> FinishFailures{},DisconnectFailures{};Handle TestCallbackRelease{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
#endif
    struct Fetch {CF_OPERATION_INFO Operation{};std::string Id,Entry;long long Offset{},Length{},Start{},End{},Next{};bool Canceled{},Done{};std::mutex Gate;};
    struct OpenState{int Count{};unsigned long long Closed{},Sequence{};};unsigned long long ActivitySequence{};
    std::map<std::string,std::shared_ptr<Fetch>> Fetches;std::map<std::string,OpenState> Opens;std::set<std::string> Blocked;std::map<std::string,ULONGLONG> LastReads;
    explicit Impl(RuntimeHost& host,const Json& root):Host(host),Root(root),Mount(GuidN(root.at("mountId"))),Session(GuidN(root.at("sessionId"))),Clearing(root.value("blockHydration",false)){}
    bool Event(const char* name,Json data,bool required=true){Json value{{"kind","event"},{"mountId",Mount},{"sessionId",Session},{"event",name},{"data",std::move(data)}};if(!required)return Host.Send(std::move(value),false);return Host.SendCallback(std::move(value),CallbackDeadline?CallbackDeadline:GetTickCount64()+45);}
    std::string Entry(const CF_CALLBACK_INFO& info){Require(info.FileIdentity&&info.FileIdentityLength>0&&info.FileIdentityLength<=4096);auto marker=Parse(std::string(static_cast<const char*>(info.FileIdentity),info.FileIdentityLength),4096);Require(GuidN(marker.at("MountId"))==Mount,E_ACCESSDENIED);auto id=marker.at("EntryId").get<std::string>();Require(!id.empty()&&id.size()<=4096);return id;}
    /// <summary>回调票据只在进出时持锁，允许CfExecute同步重入；断开后等待全部票据归还再销毁上下文。</summary>
    struct CallbackTicket {
        Impl* Owner{};
        explicit CallbackTicket(Impl* owner){std::lock_guard lock(owner->CallbackGate);if(!owner->Detached){++owner->Callbacks;Owner=owner;}}
        ~CallbackTicket(){if(Owner){std::lock_guard lock(Owner->CallbackGate);--Owner->Callbacks;Owner->CallbackWake.notify_all();}}
    };
    void Detach(){std::unique_lock lock(CallbackGate);Detached=true;CallbackWake.wait(lock,[this]{return Callbacks==0;});}
    template<CF_CALLBACK_TYPE Type> static void CALLBACK Callback(const CF_CALLBACK_INFO* info,const CF_CALLBACK_PARAMETERS* parameters)noexcept{
        if(!info||!parameters||!info->CallbackContext)return;auto self=static_cast<Impl*>(const_cast<void*>(info->CallbackContext));
        const auto started=GetTickCount64();CallbackTicket callback(self);if(!callback.Owner)return;
        struct Sample{RuntimeHost& Host;ULONGLONG Started;~Sample(){Host.RecordCallbackTime(GetTickCount64()-Started);}} sample{self->Host,started};CallbackBudget budget;
        try{self->Dispatch(Type,*info,*parameters);}catch(...){try{if constexpr(Type==CF_CALLBACK_TYPE_FETCH_DATA)Complete(Operation(*info,CF_OPERATION_TYPE_TRANSFER_DATA),parameters->FetchData.RequiredFileOffset.QuadPart,parameters->FetchData.RequiredLength.QuadPart,UnavailableStatus);if constexpr(Type==CF_CALLBACK_TYPE_NOTIFY_DELETE||Type==CF_CALLBACK_TYPE_NOTIFY_RENAME)Ack(*info,Type,static_cast<NTSTATUS>(0xc0000022));self->Event("callbackFailure",{{"type",Type},{"hresult",E_FAIL}});}catch(...){self->Host.FailCallbacks();}}
    }
    void Dispatch(CF_CALLBACK_TYPE type,const CF_CALLBACK_INFO& info,const CF_CALLBACK_PARAMETERS& parameters){
        auto entry=Entry(info);
        if(type==CF_CALLBACK_TYPE_FETCH_DATA){
            auto fetch=std::make_shared<Fetch>();fetch->Operation=Operation(info,CF_OPERATION_TYPE_TRANSFER_DATA);fetch->Id=NewId();fetch->Entry=entry;fetch->Offset=parameters.FetchData.RequiredFileOffset.QuadPart;fetch->Length=parameters.FetchData.RequiredLength.QuadPart;Require(fetch->Offset>=0&&fetch->Length>0&&info.FileSize.QuadPart>=fetch->Offset&&fetch->Length<=info.FileSize.QuadPart-fetch->Offset);
            fetch->Start=fetch->Offset/4096*4096;const auto end=fetch->Offset+fetch->Length;fetch->End=end>LLONG_MAX-4095?info.FileSize.QuadPart:std::min(info.FileSize.QuadPart,(end+4095)/4096*4096);fetch->Next=fetch->Start;
            bool blocked;{std::lock_guard lock(Gate);blocked=Clearing||Stopped||Blocked.contains(entry);if(!blocked){Fetches.emplace(fetch->Id,fetch);LastReads[entry]=GetTickCount64();}}
            // 驱动完成可能同步重入；不持会话状态锁执行CfExecute。
            if(blocked){Complete(fetch->Operation,fetch->Offset,fetch->Length,CanceledStatus);return;}
            const auto pid=info.ProcessInfo?info.ProcessInfo->ProcessId:0;std::wstring image=info.ProcessInfo&&info.ProcessInfo->ImagePath?PathFindFileNameW(info.ProcessInfo->ImagePath):L"unknown";if(image.size()>260)image=L"unknown";
            if(!Event("fetch",{{"fetchId",fetch->Id},{"entryId",entry},{"offset",std::to_string(fetch->Offset)},{"length",std::to_string(fetch->Length)},{"priority",info.PriorityHint},{"explicit",(parameters.FetchData.Flags&CF_CALLBACK_FETCH_DATA_FLAG_EXPLICIT_HYDRATION)!=0},{"processId",pid},{"image",Utf8(image)}},false)){
                {std::lock_guard lock(Gate);Fetches.erase(fetch->Id);}Complete(fetch->Operation,fetch->Offset,fetch->Length,UnavailableStatus);
            }return;
        }
        if(type==CF_CALLBACK_TYPE_CANCEL_FETCH_DATA){
            std::vector<std::shared_ptr<Fetch>> canceled;{std::lock_guard lock(Gate);for(auto& [_,fetch]:Fetches){auto offset=parameters.Cancel.FetchData.FileOffset.QuadPart,length=parameters.Cancel.FetchData.Length.QuadPart;bool covered=offset>=0&&offset<=fetch->Offset&&(length==-1||(length>0&&fetch->Offset-offset<=length&&fetch->Length<=length-(fetch->Offset-offset)));if(fetch->Operation.TransferKey.QuadPart==info.TransferKey.QuadPart&&fetch->Operation.RequestKey.QuadPart==info.RequestKey.QuadPart&&covered)canceled.push_back(fetch);}}
            for(auto& fetch:canceled){{std::lock_guard lock(fetch->Gate);fetch->Canceled=true;}Event("cancelFetch",{{"fetchId",fetch->Id},{"entryId",fetch->Entry}});}return;
        }
        const auto self=info.ProcessInfo&&(info.ProcessInfo->ProcessId==GetCurrentProcessId()||info.ProcessInfo->ProcessId==Host.ParentProcessId());
        if(type==CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION||type==CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION){if(self)return;OpenState state;{std::lock_guard lock(Gate);if(Stopped||Clearing)return;auto& current=Opens[entry];if(type==CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION)++current.Count;else{current.Count=std::max(0,current.Count-1);++current.Closed;}current.Sequence=++ActivitySequence;state=current;}Event("activity",{{"entryId",entry},{"openCount",state.Count},{"closeCount",std::to_string(state.Closed)},{"sequence",std::to_string(state.Sequence)}});return;}
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
CloudSession::~CloudSession()noexcept{try{Stop();}catch(...){if(_impl->Connection.Internal){_impl->Detach();
    // 断开失败时保留驱动持有的回调上下文；已在途回调排空后不再访问Host，内存由进程退出回收。
    _impl.release();}}}
const Json& CloudSession::Root()const{return _impl->Root;}
void CloudSession::Connect(){auto& p=*_impl;std::lock_guard connection(p.ConnectionGate);{std::lock_guard lock(p.Gate);Require(!p.Stopped,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));}CF_CALLBACK_REGISTRATION callbacks[]{
    {CF_CALLBACK_TYPE_FETCH_DATA,Impl::Callback<CF_CALLBACK_TYPE_FETCH_DATA>},{CF_CALLBACK_TYPE_CANCEL_FETCH_DATA,Impl::Callback<CF_CALLBACK_TYPE_CANCEL_FETCH_DATA>},
    {CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION>},{CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION>},
    {CF_CALLBACK_TYPE_NOTIFY_DELETE,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_DELETE>},{CF_CALLBACK_TYPE_NOTIFY_DELETE_COMPLETION,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_DELETE_COMPLETION>},
    {CF_CALLBACK_TYPE_NOTIFY_RENAME,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_RENAME>},{CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION,Impl::Callback<CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION>},CF_CALLBACK_REGISTRATION_END};
    Check(CfConnectSyncRoot(Text(p.Root,"rootPath").c_str(),callbacks,&p,CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO|CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,&p.Connection));
}
bool CloudSession::Active(const std::string& entry)const{auto& p=*_impl;std::lock_guard lock(p.Gate);if(p.Opens.contains(entry)&&p.Opens.at(entry).Count>0)return true;for(auto& [_,fetch]:p.Fetches)if(fetch->Entry==entry)return true;return false;}
Json CloudSession::Activity(const std::string& entry)const{return {{"active",Active(entry)}};}
void CloudSession::SetBlocked(const std::string& entry,bool blocked){auto& p=*_impl;std::lock_guard lock(p.Gate);if(blocked)p.Blocked.insert(entry);else p.Blocked.erase(entry);}
bool CloudSession::OwnsFetch(const std::string& id)const{std::lock_guard lock(_impl->Gate);return _impl->Fetches.contains(id);}
void CloudSession::Transfer(const RuntimeBlock& block){
    std::shared_ptr<Impl::Fetch> fetch;{std::lock_guard lock(_impl->Gate);auto found=_impl->Fetches.find(block.FetchId);Require(found!=_impl->Fetches.end(),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));fetch=found->second;}
    std::lock_guard lock(fetch->Gate);Require(!fetch->Canceled&&!fetch->Done,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));Require(block.Offset==fetch->Next&&block.Offset>=fetch->Start&&block.Offset<=fetch->End&&static_cast<long long>(block.Bytes.size())<=fetch->End-block.Offset);
    Check(Complete(fetch->Operation,block.Offset,static_cast<long long>(block.Bytes.size()),0,block.Bytes.data()));fetch->Next+=block.Bytes.size();
}
/// <summary>确认完成之后才退休请求；CfExecute瞬态失败保留同一请求供幂等重试，不提前解除活动保护。</summary>
void CloudSession::Finish(const std::string& id,NTSTATUS status){
    std::shared_ptr<Impl::Fetch> fetch;{std::lock_guard lock(_impl->Gate);auto found=_impl->Fetches.find(id);if(found==_impl->Fetches.end())return;fetch=found->second;}
    {std::lock_guard lock(fetch->Gate);if(fetch->Done)return;
#ifdef HYBRIDMOUNT_NATIVE_TESTS
        if(_impl->FinishFailures.load()>0){--_impl->FinishFailures;throw Failure(HRESULT_FROM_WIN32(ERROR_BUSY));}
#endif
        if(!(status==0&&!fetch->Canceled&&fetch->Next>=fetch->Offset+fetch->Length)){
            if(status==0)status=UnavailableStatus;auto code=Complete(fetch->Operation,fetch->Offset,fetch->Length,status);
            if(!CanceledCompletion(code)&&code!=HRESULT_FROM_WIN32(ERROR_NOT_FOUND))Check(code);
        }
        fetch->Done=true;
    }
    {std::lock_guard lock(_impl->Gate);auto found=_impl->Fetches.find(id);if(found!=_impl->Fetches.end()&&found->second==fetch)_impl->Fetches.erase(found);}
}
void CloudSession::BeginClear(){auto& p=*_impl;std::vector<std::string> ids;{std::lock_guard lock(p.Gate);p.Clearing=true;for(auto& [id,fetch]:p.Fetches){ids.push_back(id);}}
    for(auto& id:ids){{std::shared_ptr<Impl::Fetch> fetch;{std::lock_guard lock(p.Gate);auto found=p.Fetches.find(id);if(found!=p.Fetches.end())fetch=found->second;}if(fetch){std::lock_guard lock(fetch->Gate);fetch->Canceled=true;}}p.Event("cancelFetch",{{"fetchId",id}});try{Finish(id,CanceledStatus);}catch(...){}}
}
void CloudSession::Stop(){auto& p=*_impl;std::lock_guard connection(p.ConnectionGate);{std::lock_guard lock(p.Gate);if(p.Stopped&&!p.Connection.Internal)return;p.Stopped=true;}BeginClear();if(p.Connection.Internal){
#ifdef HYBRIDMOUNT_NATIVE_TESTS
    if(p.DisconnectFailures.load()>0){--p.DisconnectFailures;throw Failure(HRESULT_FROM_WIN32(ERROR_BUSY));}
#endif
    Check(CfDisconnectSyncRoot(p.Connection));p.Connection={};
    // 平台停止新增回调后仍须排空已进入的回调，再允许销毁其上下文；不能把API返回当作回调已退栈。
    p.Detach();std::lock_guard lock(p.Gate);p.Fetches.clear();p.Opens.clear();}}
}

#ifdef HYBRIDMOUNT_NATIVE_TESTS
namespace hm {
void CloudSession::HoldTestCallback(){Impl::CallbackTicket callback(_impl.get());Require(callback.Owner!=nullptr);_impl->Event("testCallback",Json::object());Require(WaitForSingleObject(_impl->TestCallbackRelease.Value,10000)==WAIT_OBJECT_0,HRESULT_FROM_WIN32(ERROR_TIMEOUT));}
void CloudSession::ReleaseTestCallback(){WinCheck(SetEvent(_impl->TestCallbackRelease.Value));}
void CloudSession::SetTestFailures(int finish,int disconnect){Require(finish>=0&&finish<=3&&disconnect>=0&&disconnect<=3);_impl->FinishFailures=finish;_impl->DisconnectFailures=disconnect;}}
#endif

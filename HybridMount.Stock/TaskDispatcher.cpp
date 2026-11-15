#include "TaskDispatcher.h"
#include "NativeFile.h"
#include "RuntimeOperations.h"
#include <algorithm>
#include <array>
#include <map>
#include <vector>

namespace hm {
namespace {
std::string NewTaskId(){GUID id{};Check(CoCreateGuid(&id));wchar_t raw[40]{};Require(StringFromGUID2(id,raw,40)>0);return GuidN(Utf8(raw));}
struct PathLess {
    bool operator()(const std::wstring& a,const std::wstring& b)const noexcept {
        return CompareStringOrdinal(a.data(),static_cast<int>(a.size()),b.data(),static_cast<int>(b.size()),TRUE)==CSTR_LESS_THAN;
    }
};
bool RelativeWatchPath(std::wstring& path){
    std::replace(path.begin(),path.end(),L'\\',L'/');
    if(path.empty()||path.size()>4096||path.front()==L'/'||path.find(L':')!=std::wstring::npos||path.find(L'\0')!=std::wstring::npos)return false;
    size_t begin=0;
    while(begin<=path.size()){auto end=path.find(L'/',begin);auto part=path.substr(begin,end==std::wstring::npos?path.size()-begin:end-begin);if(part.empty()||part==L"."||part==L"..")return false;if(end==std::wstring::npos)break;begin=end+1;}
    return true;
}
}
struct TaskDispatcher::Impl {
    struct Change {std::wstring Path,Old;std::string Kind;ULONGLONG First{},Last{};};
    struct Watch {
        Json Root;std::string Mount,Session;Handle Directory;OVERLAPPED Io{};std::array<BYTE,65536> Buffer{};
        std::map<std::wstring,Change,PathLess> Changes;std::wstring RenameOld;ULONGLONG RenameAt{};long long Sequence{};
        bool Pending{},Stopping{},Dirty{};size_t Bytes{};
    };
    struct Intent {Json Frame;std::string Session;bool Accepted{};ULONGLONG Sent{};size_t Bytes{};};
    RuntimeHost& Host;Handle Port{CreateIoCompletionPort(INVALID_HANDLE_VALUE,nullptr,0,1)};
    mutable std::mutex Gate;std::condition_variable Wake,Drained;std::map<std::string,std::shared_ptr<Watch>> Watches;
    std::map<std::string,Intent> Intents;size_t PathCount{},PathBytes{},IntentBytes{};bool Stopping{};
    std::thread Completion,Dispatch;
    unsigned long long Events{},Overflows{},Unpaired{},Sent{},Accepted{},Results{},Rejected{};
    explicit Impl(RuntimeHost& host):Host(host){WinCheck(Port.Value!=nullptr);Completion=std::thread([this]{Complete();});Dispatch=std::thread([this]{Pump();});}
    void Issue(const std::shared_ptr<Watch>& watch){
        watch->Io={};watch->Pending=true;
        if(!ReadDirectoryChangesW(watch->Directory.Value,watch->Buffer.data(),static_cast<DWORD>(watch->Buffer.size()),TRUE,
            FILE_NOTIFY_CHANGE_FILE_NAME|FILE_NOTIFY_CHANGE_DIR_NAME|FILE_NOTIFY_CHANGE_ATTRIBUTES|FILE_NOTIFY_CHANGE_SIZE|FILE_NOTIFY_CHANGE_LAST_WRITE|FILE_NOTIFY_CHANGE_CREATION,
            nullptr,&watch->Io,nullptr)){
            watch->Pending=false;watch->Dirty=true;++Overflows;
        }
    }
    void ClearChanges(Watch& watch){PathCount-=watch.Changes.size();PathBytes-=watch.Bytes;watch.Changes.clear();watch.Bytes=0;}
    void Dirty(Watch& watch){ClearChanges(watch);watch.Dirty=true;watch.RenameOld.clear();++Overflows;Wake.notify_all();}
    void Record(Watch& watch,std::wstring path,std::string kind,std::wstring old={}){
        if(!RelativeWatchPath(path)||!old.empty()&&!RelativeWatchPath(old)){Dirty(watch);return;}
        if(watch.Dirty)return;
        const auto now=GetTickCount64();
        // 改名因果立即冻结入独立队列，普通写入仍去抖；避免关闭补扫先把原上传误判为删除。
        if(kind=="renamed"){
            if(!Freeze(watch,Change{std::move(path),std::move(old),std::move(kind),now,now}))Dirty(watch);
            Wake.notify_all();return;
        }
        auto found=watch.Changes.find(path);
        // 改名不可与普通写事件互相覆盖；先发已有因果，再保存新意图。
        if(found!=watch.Changes.end()&&(kind=="renamed"||found->second.Kind=="renamed")){
            if(!Freeze(watch,found->second)){Dirty(watch);return;}
            PathBytes-=found->second.Path.size()*sizeof(wchar_t)+found->second.Old.size()*sizeof(wchar_t);watch.Bytes-=found->second.Path.size()*sizeof(wchar_t)+found->second.Old.size()*sizeof(wchar_t);
            --PathCount;watch.Changes.erase(found);found=watch.Changes.end();
        }
        if(found!=watch.Changes.end()){
            found->second.Last=now;if(found->second.Kind!="created"||kind=="deleted")found->second.Kind=std::move(kind);return;
        }
        const auto bytes=(path.size()+old.size())*sizeof(wchar_t);
        if(PathCount>=4096||PathBytes+bytes>8*1024*1024){Dirty(watch);return;}
        watch.Bytes+=bytes;PathBytes+=bytes;++PathCount;
        auto key=path;watch.Changes.emplace(std::move(key),Change{std::move(path),std::move(old),std::move(kind),now,now});Wake.notify_all();
    }
    bool Freeze(Watch& watch,const Change& change){
        const auto reserve=change.Kind=="rescan"?4096u:4096u-static_cast<unsigned>(std::min<size_t>(1024,Watches.size()));
        if(Intents.size()>=reserve)return false;
        const auto id=NewTaskId();auto sequence=watch.Sequence+1;
        Json frame={{"kind","intent"},{"taskId",id},{"mountId",watch.Mount},{"sessionId",watch.Session},{"sequence",std::to_string(sequence)},
            {"eventKind",change.Kind},{"path",Utf8(change.Path)},{"oldPath",change.Old.empty()?Json(nullptr):Json(Utf8(change.Old))}};
        const auto bytes=frame.dump().size();const auto reservedBytes=change.Kind=="rescan"?0u:512u*Watches.size();if(IntentBytes+bytes+reservedBytes>8*1024*1024)return false;
        watch.Sequence=sequence;IntentBytes+=bytes;Intents.emplace(id,Intent{std::move(frame),watch.Session,false,0,bytes});return true;
    }
    /// <summary>完成线程不等待管道、不查询根和正文；缓冲解析失败转为可靠补扫。</summary>
    void Complete()noexcept {
        while(true){DWORD bytes{};ULONG_PTR key{};OVERLAPPED* io{};const auto ok=GetQueuedCompletionStatus(Port.Value,&bytes,&key,&io,INFINITE);
            if(!io&&key==0)return;
            std::lock_guard lock(Gate);auto* raw=reinterpret_cast<Watch*>(key);auto found=std::find_if(Watches.begin(),Watches.end(),[&](const auto& item){return item.second.get()==raw;});
            if(found==Watches.end())continue;auto watch=found->second;watch->Pending=false;Drained.notify_all();
            if(watch->Stopping)continue;
            try{
                if(!ok||bytes==0){Dirty(*watch);}else {
                    size_t offset=0;
                    while(offset<bytes){
                        Require(bytes-offset>=offsetof(FILE_NOTIFY_INFORMATION,FileName));auto* item=reinterpret_cast<const FILE_NOTIFY_INFORMATION*>(watch->Buffer.data()+offset);
                        Require(item->FileNameLength%sizeof(wchar_t)==0&&item->FileNameLength<=bytes-offset- offsetof(FILE_NOTIFY_INFORMATION,FileName));
                        std::wstring path(item->FileName,item->FileNameLength/sizeof(wchar_t));++Events;
                        switch(item->Action){
                        case FILE_ACTION_RENAMED_OLD_NAME:if(!watch->RenameOld.empty()){++Unpaired;Dirty(*watch);}watch->RenameOld=std::move(path);watch->RenameAt=GetTickCount64();break;
                        case FILE_ACTION_RENAMED_NEW_NAME:if(watch->RenameOld.empty()){++Unpaired;Dirty(*watch);}else{auto old=std::move(watch->RenameOld);watch->RenameOld.clear();Record(*watch,std::move(path),"renamed",std::move(old));}break;
                        case FILE_ACTION_ADDED:Record(*watch,std::move(path),"created");break;
                        case FILE_ACTION_REMOVED:Record(*watch,std::move(path),"deleted");break;
                        case FILE_ACTION_MODIFIED:Record(*watch,std::move(path),"changed");break;
                        default:Dirty(*watch);break;
                        }
                        if(item->NextEntryOffset==0)break;
                        Require(item->NextEntryOffset>=offsetof(FILE_NOTIFY_INFORMATION,FileName)&&item->NextEntryOffset%4==0&&item->NextEntryOffset<bytes-offset);offset+=item->NextEntryOffset;
                    }
                }
                if(ok||GetLastError()!=ERROR_OPERATION_ABORTED)Issue(watch);
            }catch(...){Dirty(*watch);Issue(watch);}
        }
    }
    /// <summary>独立合并线程200ms去抖、2秒上限；未确认意图以同ID重发，不阻塞IOCP。</summary>
    void Pump()noexcept {
        std::unique_lock lock(Gate);
        while(!Stopping){
            try{
                const auto now=GetTickCount64();
                for(auto& [_,watch]:Watches){
                    if(watch->Stopping)continue;
                    if(!watch->RenameOld.empty()&&now-watch->RenameAt>=2000){++Unpaired;Dirty(*watch);}
                    if(watch->Dirty){if(Freeze(*watch,Change{{},{},"rescan",now,now}))watch->Dirty=false;}
                    else for(auto it=watch->Changes.begin();it!=watch->Changes.end();){
                        if(now-it->second.Last<200&&now-it->second.First<2000){++it;continue;}
                        if(!Freeze(*watch,it->second))break;
                        const auto bytes=(it->second.Path.size()+it->second.Old.size())*sizeof(wchar_t);watch->Bytes-=bytes;PathBytes-=bytes;--PathCount;it=watch->Changes.erase(it);
                    }
                }
                // 冻结顺序以每根序号为准；不能让随机GUID排序造成正常序号断档。
                std::vector<Intent*> send;for(auto& [_,intent]:Intents)if(!intent.Accepted&&(intent.Sent==0||now-intent.Sent>=1000))send.push_back(&intent);
                std::sort(send.begin(),send.end(),[](const auto* a,const auto* b){if(a->Session!=b->Session)return a->Session<b->Session;return Number(a->Frame,"sequence")<Number(b->Frame,"sequence");});
                for(auto* intent:send)if(Host.SendTask(intent->Frame)){intent->Sent=now;++Sent;}
            }catch(...){++Rejected;}
            Wake.wait_for(lock,std::chrono::milliseconds(50));
        }
    }
};
TaskDispatcher::TaskDispatcher(RuntimeHost& host):_impl(std::make_unique<Impl>(host)){}
TaskDispatcher::~TaskDispatcher(){Stop();}
void TaskDispatcher::Add(const Json& root){
    auto& p=*_impl;auto watch=std::make_shared<Impl::Watch>();watch->Root=root;watch->Mount=GuidN(root.at("mountId"));watch->Session=GuidN(root.at("sessionId"));
    const auto path=FullPath(Text(root,"rootPath"));NoLinks(path,true);
    watch->Directory.Value=CreateFileW(path.c_str(),FILE_LIST_DIRECTORY|SYNCHRONIZE,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OVERLAPPED|FILE_FLAG_OPEN_REPARSE_POINT,nullptr);WinCheck(watch->Directory.Value!=INVALID_HANDLE_VALUE);
    std::lock_guard lock(p.Gate);Require(!p.Stopping&&p.Watches.size()<1024,HRESULT_FROM_WIN32(ERROR_BUSY));Require(!p.Watches.contains(watch->Session),HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS));
    WinCheck(CreateIoCompletionPort(watch->Directory.Value,p.Port.Value,reinterpret_cast<ULONG_PTR>(watch.get()),0)!=nullptr);p.Watches.emplace(watch->Session,watch);p.Issue(watch);p.Wake.notify_all();
}
long long TaskDispatcher::Remove(const std::string& session){
    auto& p=*_impl;std::unique_lock lock(p.Gate);auto found=p.Watches.find(session);if(found==p.Watches.end())return 0;auto watch=found->second;
    watch->Stopping=true;if(watch->Pending)CancelIoEx(watch->Directory.Value,&watch->Io);
    p.Drained.wait(lock,[&]{return !watch->Pending;});
    // 一条最终补扫意图覆盖已经捕获的全部变化，保留此前已冻结意图，不无限等待业务完成。
    Require(p.Freeze(*watch,Impl::Change{{},{},"rescan",0,0}),HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA));p.ClearChanges(*watch);
    auto sequence=watch->Sequence;p.Watches.erase(session);p.Wake.notify_all();return sequence;
}
void TaskDispatcher::Receive(const Json& frame){
    auto& p=*_impl;const auto kind=frame.at("kind").get<std::string>();const auto id=GuidN(frame.at("taskId"));auto mount=GuidN(frame.at("mountId"));auto session=GuidN(frame.at("sessionId"));
    std::lock_guard lock(p.Gate);auto found=p.Intents.find(id);
    if(found!=p.Intents.end())Require(found->second.Session==session&&found->second.Frame.at("mountId")==mount,E_ACCESSDENIED);
    if(kind=="sessionRetired"){
        // .NET只在停止监听并确认连续序号持久接纳后发送；不删除任何业务记录或执行文件动作。
        Require(!p.Watches.contains(session),E_ACCESSDENIED);
        for(auto it=p.Intents.begin();it!=p.Intents.end();){if(it->second.Session==session&&it->second.Frame.at("mountId")==mount){p.IntentBytes-=it->second.Bytes;it=p.Intents.erase(it);}else ++it;}
    }else if(kind=="intentAck"){
        auto status=frame.at("status").get<std::string>();Require(status=="accepted"||status=="noop"||status=="deferred"||status=="rejected");
        if(found!=p.Intents.end()){
            if(status=="accepted"){found->second.Accepted=true;++p.Accepted;}
            else if(status=="noop"||status=="rejected"){p.IntentBytes-=found->second.Bytes;p.Intents.erase(found);}
            else found->second.Sent=GetTickCount64();
        }
    }else if(kind=="taskResult"){
        const auto result=GuidN(frame.at("resultId"));Require(frame.at("stage").is_string()&&frame.at("stage").get<std::string>().size()<=64&&frame.at("outcome").is_string()&&frame.at("outcome").get<std::string>().size()<=64);
        if(frame.at("final").get<bool>()&&found!=p.Intents.end()){p.IntentBytes-=found->second.Bytes;p.Intents.erase(found);}++p.Results;
        // 新进程也确认旧代次的持久结果；该结果不执行文件操作，不改变当前目录状态。
        p.Host.SendTask({{"kind","resultAck"},{"resultId",result},{"taskId",id},{"mountId",mount},{"sessionId",session}});
    }else Require(false,E_INVALIDARG);
    p.Wake.notify_all();
}
Json TaskDispatcher::Statistics()const{auto& p=*_impl;std::lock_guard lock(p.Gate);return {{"watches",p.Watches.size()},{"queuedPaths",p.PathCount},{"pathBytes",p.PathBytes},{"pendingIntents",p.Intents.size()},{"intentBytes",p.IntentBytes},{"events",p.Events},{"overflows",p.Overflows},{"unpairedRenames",p.Unpaired},{"sent",p.Sent},{"accepted",p.Accepted},{"results",p.Results},{"rejected",p.Rejected}};}
void TaskDispatcher::Stop(){
    auto& p=*_impl;{std::unique_lock lock(p.Gate);if(p.Stopping)return;for(auto& [_,watch]:p.Watches){watch->Stopping=true;if(watch->Pending)CancelIoEx(watch->Directory.Value,&watch->Io);}p.Drained.wait(lock,[&]{return std::all_of(p.Watches.begin(),p.Watches.end(),[](const auto& item){return !item.second->Pending;});});p.Stopping=true;p.Wake.notify_all();}
    PostQueuedCompletionStatus(p.Port.Value,0,0,nullptr);if(p.Completion.joinable())p.Completion.join();if(p.Dispatch.joinable())p.Dispatch.join();std::lock_guard lock(p.Gate);p.Watches.clear();
}
#ifdef HYBRIDMOUNT_NATIVE_TESTS
void TaskDispatcher::OverflowForTests(const std::string& session){std::lock_guard lock(_impl->Gate);auto found=_impl->Watches.find(session);Require(found!=_impl->Watches.end());_impl->Dirty(*found->second);}
#endif
}

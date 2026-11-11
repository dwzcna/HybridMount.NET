#include "LocalReadLeases.h"
#include <set>
#include "LocalReadIo.h"
#include "NativeDiagnostics.h"
namespace hm {
namespace {
struct Lease {
    std::shared_ptr<LocalReadFile> File;HANDLE Raw{};Json Root;std::wstring Path;FileState Before;Placeholder Cloud;std::mutex Gate;int Requests{};long long Bytes{};ULONGLONG ConfirmationDeadline{};bool Confirmed{};std::atomic<bool> Closed{};DWORD ReadBudget=10000;LocalReadFault Fault;
    explicit Lease(const Json& data):Root(data){
        ValidateRuntimeRoot(data);Path=FullPath(Text(data,"path"));auto root=FullPath(Text(data,"rootPath"));Require(Path.size()>root.size()&&Path[root.size()]==static_cast<wchar_t>(92)&&SamePath(root,Path.substr(0,root.size())),E_ACCESSDENIED);NoLinks(Path,true);
        auto probe=OpenAttributes(Path);auto info=InspectPlaceholder(probe.Value);auto marker=Parse(std::string(info.Identity.begin(),info.Identity.end()),4096);Require(GuidN(marker.at("MountId"))==GuidN(data.at("mountId"))&&marker.at("EntryId")==data.at("entryId"),E_ACCESSDENIED);auto stamp=InspectFile(probe.Value);Require(!stamp.Directory&&HasFullContent(probe.Value,stamp.Length));
        File=std::make_shared<LocalReadFile>(Path,true);Raw=File->Raw;try{Before=InspectFile(Raw);Require(Before.Identity==stamp.Identity,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
#ifdef HYBRIDMOUNT_NATIVE_TESTS
        ReadBudget=data.value("testReadBudgetMs",10000u);Fault.CompletionDelay=data.value("testCompletionDelayMs",0u);Fault.Error=data.value("testReadError",0u);Fault.Short=data.value("testShortRead",false);Require(ReadBudget>=20&&ReadBudget<=10000&&Fault.CompletionDelay<=10000);
#endif
        if(data.contains("expected")){auto& expected=data.at("expected");Require(Before.Identity==expected.at("identity").get<std::string>()&&Before.Length==Number(expected,"length")&&Before.LastWrite==Number(expected,"lastWriteTime")&&(Before.Usn==Number(expected,"usn")||(Before.Reasons&0x77)==0),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));}
        else Require(Before.Key()==data.at("expectedStamp").get<std::string>(),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));info=InspectPlaceholder(Raw);Require(info.Identity==InspectPlaceholder(probe.Value).Identity&&HasFullContent(Raw,Before.Length));Cloud=info;}catch(...){Close();throw;}
    }
    /// <summary>保护引用内允许只读访问产生的元数据USN，正文身份、长度、修改时间及真实云状态必须不变。</summary>
    FileState Verify(){auto stamp=InspectFile(Raw);Require(File&&!File->Broken()&&stamp.Identity==Before.Identity&&stamp.Length==Before.Length&&stamp.LastWrite==Before.LastWrite&&stamp.Links==Before.Links&&!stamp.Directory,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));auto info=InspectPlaceholder(Raw);Require(info.Identity==Cloud.Identity&&info.InSync==Cloud.InSync&&info.Pin==Cloud.Pin&&info.Modified==Cloud.Modified&&info.OnDisk==Cloud.OnDisk&&HasFullContent(Raw,stamp.Length),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));return stamp;}
    /// <summary>先拒绝新读并取消，再锁外有界排空；仍在内核的操作持有File直到真正完成，不能把未完成关闭当成功发布。</summary>
    bool Close(DWORD budget=250){std::shared_ptr<LocalReadFile> file;{std::lock_guard lock(Gate);Closed=true;file=File;}if(!file)return true;file->RequestClose();return file->Drain(budget);}
    ~Lease(){if(File)File->RequestClose();}
    std::vector<BYTE> Read(RuntimeCommand& command,RuntimeHost& host,long long offset,int length){
        std::shared_ptr<LocalReadFile> file;{std::lock_guard lock(Gate);Require(Confirmed&&!Closed&&offset>=0&&offset<=Before.Length&&length>=0&&length<=256*1024&&length<=Before.Length-offset);Require(++Requests<=512&&(Bytes+=length)<=128LL*1024*1024,HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA));file=File;}
        command.CheckCancel();ValidateRuntimeRoot(Root);Verify();auto bytes=ReadLocalFile(file,offset,static_cast<DWORD>(length),command.Cancel.Value,host.StopHandle(),GetTickCount64()+ReadBudget,Fault);Verify();Require(!Closed,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));return bytes;
    }

};
}
/// <summary>未确认租约由独立可停止计时器回收；已确认租约归调用方直到close／进程停止，不以解码静默时间推断泄漏。</summary>
struct LocalReadLeases::Impl{
    RuntimeHost& Host;std::mutex Gate;std::map<std::string,std::shared_ptr<Lease>> Leases;std::set<std::string> Opening;std::map<std::string,Json> ClosedLeases;bool Stopped{};Handle StopEvent{CreateEventW(nullptr,TRUE,FALSE,nullptr)};std::thread Reaper;
    explicit Impl(RuntimeHost& host):Host(host){WinCheck(StopEvent.Value!=nullptr);Reaper=std::thread([this]{while(WaitForSingleObject(StopEvent.Value,100)==WAIT_TIMEOUT)Reap();});}
    void Reap()noexcept{try{
        std::map<std::string,std::shared_ptr<Lease>> snapshot;{std::lock_guard lock(Gate);snapshot=Leases;}
        for(auto& [key,lease]:snapshot){std::unique_lock local(lease->Gate,std::try_to_lock);if(!local.owns_lock()||lease->Closed||lease->Confirmed||GetTickCount64()<lease->ConfirmationDeadline)continue;
            {std::lock_guard lock(Gate);auto found=Leases.find(key);if(found==Leases.end()||found->second!=lease)continue;Leases.erase(found);}lease->Closed=true;local.unlock();lease->Close(0);}
    }catch(...){/* 本轮失败保留下一次回收机会；Stop仍归还全部租约。 */}}
};
LocalReadLeases::LocalReadLeases(RuntimeHost& host):_impl(std::make_unique<Impl>(host)){}
LocalReadLeases::~LocalReadLeases(){Stop();}
Json LocalReadLeases::Execute(RuntimeCommand& command){
    const auto& data=command.Data;auto& p=*_impl;
#ifdef HYBRIDMOUNT_NATIVE_TESTS
    if(command.Operation=="testLocalReadStats"){std::lock_guard lock(p.Gate);auto stats=LocalReadStatistics();stats["leases"]=p.Leases.size();stats["opening"]=p.Opening.size();return stats;}
#endif
    if(command.Operation=="localReadDiagnostics")return LocalReadStatistics();
    const auto key=GuidN(data.at("leaseId"));
    if(command.Operation=="openLocalRead"){
        {std::lock_guard lock(p.Gate);Require(!p.Stopped,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));Require(p.Leases.size()+p.Opening.size()<4&&!p.Leases.contains(key)&&!p.ClosedLeases.contains(key)&&p.Opening.insert(key).second,HRESULT_FROM_WIN32(ERROR_BUSY));}
        try{
            auto lease=std::make_shared<Lease>(data);ULONGLONG grace=30000;
#ifdef HYBRIDMOUNT_NATIVE_TESTS
            if(data.contains("testConfirmationMs")){auto value=data.at("testConfirmationMs").get<int>();Require(value>=100&&value<=30000);grace=static_cast<ULONGLONG>(value);}
#endif
            lease->ConfirmationDeadline=GetTickCount64()+grace;
            {std::lock_guard lock(p.Gate);p.Opening.erase(key);Require(!p.Stopped,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));p.Leases.emplace(key,lease);}return {{"stamp",FileJson(lease->Before)}};
        }catch(...){std::lock_guard lock(p.Gate);p.Opening.erase(key);throw;}
    }
    std::shared_ptr<Lease> lease;{std::lock_guard lock(p.Gate);
        if(command.Operation=="closeLocalRead"){auto done=p.ClosedLeases.find(key);if(done!=p.ClosedLeases.end()){Require(done->second.at("mountId")==data.at("mountId")&&done->second.value("sessionId",Json{})==data.value("sessionId",Json{}),E_ACCESSDENIED);Check(done->second.at("hresult").get<HRESULT>());return done->second.at("result");}}
        auto found=p.Leases.find(key);Require(found!=p.Leases.end(),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));lease=found->second;Require(GuidN(lease->Root.at("mountId"))==GuidN(data.at("mountId"))&&data.value("sessionId",Json{})==lease->Root.value("sessionId",Json{}),E_ACCESSDENIED);
        if(command.Operation=="closeLocalRead"){p.Leases.erase(found);if(p.ClosedLeases.size()>=32)p.ClosedLeases.erase(p.ClosedLeases.begin());p.ClosedLeases[key]={{"mountId",data.at("mountId")},{"sessionId",data.value("sessionId",Json{})},{"hresult",HRESULT_FROM_WIN32(ERROR_IO_INCOMPLETE)},{"result",Json::object()}};}}

    if(command.Operation=="confirmLocalRead"){Require(!lease->Closed,HRESULT_FROM_WIN32(ERROR_NOT_FOUND));Require(data.value("sessionId",Json{})==lease->Root.value("sessionId",Json{}),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));ValidateRuntimeRoot(lease->Root);lease->Verify();std::lock_guard lock(lease->Gate);Require(!lease->Closed,HRESULT_FROM_WIN32(ERROR_NOT_FOUND));lease->Confirmed=true;return Json::object();}
    if(command.Operation=="closeLocalRead"){
        Json result=Json::object();HRESULT code=S_OK;try{if(!lease->Close()){RecordNativeFailure("NativeRuntime",HRESULT_FROM_WIN32(ERROR_IO_INCOMPLETE),"LocalReadClosePending");throw Failure(HRESULT_FROM_WIN32(ERROR_IO_INCOMPLETE),"local-read-close-pending");}result["stamp"]=FileJson(lease->Verify());}catch(const Failure& error){code=error.Code;}catch(...){code=E_FAIL;}
        {std::lock_guard lock(p.Gate);auto found=p.ClosedLeases.find(key);if(found!=p.ClosedLeases.end()){found->second["hresult"]=code;found->second["result"]=result;}}Check(code);return result;
    }
    Require(command.Operation=="readLocalRange");Require(data.value("sessionId",Json{})==lease->Root.value("sessionId",Json{}),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));auto offset=Number(data,"offset");auto bytes=lease->Read(command,p.Host,offset,data.at("count").get<int>());command.CheckCancel();p.Host.SendBlock(GuidN(data.at("readId")),key,offset,bytes);return {{"count",bytes.size()}};
}
void LocalReadLeases::Stop(){auto& p=*_impl;std::map<std::string,std::shared_ptr<Lease>> leases;{std::lock_guard lock(p.Gate);if(p.Stopped)return;p.Stopped=true;leases.swap(p.Leases);}SetEvent(p.StopEvent.Value);for(auto& [_,lease]:leases)lease->Close(0);if(p.Reaper.joinable())p.Reaper.join();const auto end=GetTickCount64()+250;for(auto& [_,lease]:leases)if(!lease->Close(static_cast<DWORD>(end>GetTickCount64()?end-GetTickCount64():0)))RecordNativeFailure("NativeRuntime",HRESULT_FROM_WIN32(ERROR_IO_INCOMPLETE),"LocalReadStopPending");}
}

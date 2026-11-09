#include "LocalReadLeases.h"
#include <set>
namespace hm {
namespace {
struct Lease {
    HANDLE Protected{},Raw{};bool Referenced{};Json Root;std::wstring Path;FileState Before;Placeholder Cloud;std::mutex Gate;int Requests{};long long Bytes{};ULONGLONG ConfirmationDeadline{};bool Confirmed{},Closed{};
    explicit Lease(const Json& data):Root(data){
        ValidateRuntimeRoot(data);Path=FullPath(Text(data,"path"));auto root=FullPath(Text(data,"rootPath"));Require(Path.size()>root.size()&&Path[root.size()]==static_cast<wchar_t>(92)&&SamePath(root,Path.substr(0,root.size())),E_ACCESSDENIED);NoLinks(Path,true);
        auto probe=OpenAttributes(Path);auto info=InspectPlaceholder(probe.Value);auto marker=Parse(std::string(info.Identity.begin(),info.Identity.end()),4096);Require(GuidN(marker.at("MountId"))==GuidN(data.at("mountId"))&&marker.at("EntryId")==data.at("entryId"),E_ACCESSDENIED);auto stamp=InspectFile(probe.Value);Require(!stamp.Directory&&HasFullContent(probe.Value,stamp.Length));
        Check(CfOpenFileWithOplock(Path.c_str(),CF_OPEN_FILE_FLAG_EXCLUSIVE,&Protected));try{Referenced=CfReferenceProtectedHandle(Protected)!=FALSE;Require(Referenced,HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION));Raw=CfGetWin32HandleFromProtectedHandle(Protected);Before=InspectFile(Raw);Require(Before.Identity==stamp.Identity,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        if(data.contains("expected")){auto& expected=data.at("expected");Require(Before.Identity==expected.at("identity").get<std::string>()&&Before.Length==Number(expected,"length")&&Before.LastWrite==Number(expected,"lastWriteTime")&&(Before.Usn==Number(expected,"usn")||(Before.Reasons&0x77)==0),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));}
        else Require(Before.Key()==data.at("expectedStamp").get<std::string>(),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));info=InspectPlaceholder(Raw);Require(info.Identity==InspectPlaceholder(probe.Value).Identity&&HasFullContent(Raw,Before.Length));Cloud=info;}catch(...){Close();throw;}
    }
    /// <summary>保护引用内允许只读访问产生的元数据USN，正文身份、长度、修改时间及真实云状态必须不变。</summary>
    FileState Verify(){auto stamp=InspectFile(Raw);Require(Referenced&&stamp.Identity==Before.Identity&&stamp.Length==Before.Length&&stamp.LastWrite==Before.LastWrite&&stamp.Links==Before.Links&&!stamp.Directory,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));auto info=InspectPlaceholder(Raw);Require(info.Identity==Cloud.Identity&&info.InSync==Cloud.InSync&&info.Pin==Cloud.Pin&&info.Modified==Cloud.Modified&&info.OnDisk==Cloud.OnDisk&&HasFullContent(Raw,stamp.Length),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));return stamp;}
    void Close(){if(Referenced){CfReleaseProtectedHandle(Protected);Referenced=false;}if(Protected){CfCloseHandle(Protected);Protected=nullptr;}}
    ~Lease(){Close();}
    std::vector<BYTE> Read(long long offset,int length){
        std::lock_guard lock(Gate);Require(Confirmed&&!Closed&&offset>=0&&offset<=Before.Length&&length>=0&&length<=256*1024&&length<=Before.Length-offset);Require(++Requests<=512&&(Bytes+=length)<=128LL*1024*1024,HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA));ValidateRuntimeRoot(Root);Verify();
        std::vector<BYTE> bytes(length);DWORD done{};Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));WinCheck(event.Value!=nullptr);OVERLAPPED io{};io.Offset=static_cast<DWORD>(offset);io.OffsetHigh=static_cast<DWORD>(static_cast<unsigned long long>(offset)>>32);io.hEvent=reinterpret_cast<HANDLE>(reinterpret_cast<ULONG_PTR>(event.Value)|1);
        if(length&&!ReadFile(Raw,bytes.data(),static_cast<DWORD>(length),&done,&io)){auto code=GetLastError();Require(code==ERROR_IO_PENDING,HRESULT_FROM_WIN32(code));WinCheck(GetOverlappedResult(Raw,&io,&done,TRUE));}Require(done==static_cast<DWORD>(length));Verify();return bytes;
    }
};
}
/// <summary>未确认租约由独立可停止计时器回收；已确认租约归调用方直到close／进程停止，不以解码静默时间推断泄漏。</summary>
struct LocalReadLeases::Impl{
    RuntimeHost& Host;std::mutex Gate;std::map<std::string,std::shared_ptr<Lease>> Leases;std::set<std::string> Opening;bool Stopped{};Handle StopEvent{CreateEventW(nullptr,TRUE,FALSE,nullptr)};std::thread Reaper;
    explicit Impl(RuntimeHost& host):Host(host){WinCheck(StopEvent.Value!=nullptr);Reaper=std::thread([this]{while(WaitForSingleObject(StopEvent.Value,100)==WAIT_TIMEOUT)Reap();});}
    void Reap()noexcept{try{
        std::map<std::string,std::shared_ptr<Lease>> snapshot;{std::lock_guard lock(Gate);snapshot=Leases;}
        for(auto& [key,lease]:snapshot){std::unique_lock local(lease->Gate,std::try_to_lock);if(!local.owns_lock()||lease->Closed||lease->Confirmed||GetTickCount64()<lease->ConfirmationDeadline)continue;
            {std::lock_guard lock(Gate);auto found=Leases.find(key);if(found==Leases.end()||found->second!=lease)continue;Leases.erase(found);}lease->Closed=true;lease->Close();}
    }catch(...){/* 本轮失败保留下一次回收机会；Stop仍归还全部租约。 */}}
};
LocalReadLeases::LocalReadLeases(RuntimeHost& host):_impl(std::make_unique<Impl>(host)){}
LocalReadLeases::~LocalReadLeases(){Stop();}
Json LocalReadLeases::Execute(RuntimeCommand& command){
    const auto& data=command.Data;auto& p=*_impl;
#ifdef HYBRIDMOUNT_NATIVE_TESTS
    if(command.Operation=="testLocalReadStats"){std::lock_guard lock(p.Gate);return {{"leases",p.Leases.size()},{"opening",p.Opening.size()}};}
#endif
    const auto key=GuidN(data.at("leaseId"));
    if(command.Operation=="openLocalRead"){
        {std::lock_guard lock(p.Gate);Require(!p.Stopped,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));Require(p.Leases.size()+p.Opening.size()<4&&!p.Leases.contains(key)&&p.Opening.insert(key).second,HRESULT_FROM_WIN32(ERROR_BUSY));}
        try{
            auto lease=std::make_shared<Lease>(data);ULONGLONG grace=30000;
#ifdef HYBRIDMOUNT_NATIVE_TESTS
            if(data.contains("testConfirmationMs")){auto value=data.at("testConfirmationMs").get<int>();Require(value>=100&&value<=30000);grace=static_cast<ULONGLONG>(value);}
#endif
            lease->ConfirmationDeadline=GetTickCount64()+grace;
            {std::lock_guard lock(p.Gate);p.Opening.erase(key);Require(!p.Stopped,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));p.Leases.emplace(key,lease);}return {{"stamp",FileJson(lease->Before)}};
        }catch(...){std::lock_guard lock(p.Gate);p.Opening.erase(key);throw;}
    }
    std::shared_ptr<Lease> lease;{std::lock_guard lock(p.Gate);auto found=p.Leases.find(key);Require(found!=p.Leases.end(),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));lease=found->second;Require(GuidN(lease->Root.at("mountId"))==GuidN(data.at("mountId")),E_ACCESSDENIED);if(command.Operation=="closeLocalRead")p.Leases.erase(found);}
    if(command.Operation=="confirmLocalRead"){std::lock_guard lock(lease->Gate);Require(!lease->Closed,HRESULT_FROM_WIN32(ERROR_NOT_FOUND));Require(data.value("sessionId",Json{})==lease->Root.value("sessionId",Json{}),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));ValidateRuntimeRoot(lease->Root);lease->Verify();lease->Confirmed=true;return Json::object();}
    if(command.Operation=="closeLocalRead"){std::lock_guard lock(lease->Gate);lease->Closed=true;try{auto stamp=lease->Verify();lease->Close();return {{"stamp",FileJson(stamp)}};}catch(...){lease->Close();throw;}}
    Require(command.Operation=="readLocalRange");Require(data.value("sessionId",Json{})==lease->Root.value("sessionId",Json{}),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));auto offset=Number(data,"offset");auto bytes=lease->Read(offset,data.at("count").get<int>());command.CheckCancel();p.Host.SendBlock(GuidN(data.at("readId")),key,offset,bytes);return {{"count",bytes.size()}};
}
void LocalReadLeases::Stop(){auto& p=*_impl;{std::lock_guard lock(p.Gate);if(p.Stopped)return;p.Stopped=true;}SetEvent(p.StopEvent.Value);if(p.Reaper.joinable())p.Reaper.join();std::map<std::string,std::shared_ptr<Lease>> leases;{std::lock_guard lock(p.Gate);leases.swap(p.Leases);}for(auto& [_,lease]:leases){std::lock_guard lock(lease->Gate);lease->Closed=true;lease->Close();}}
}

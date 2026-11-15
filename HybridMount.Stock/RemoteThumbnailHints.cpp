#include "RemoteThumbnailHints.h"
#include "LocalThumbnail.h"
#include "NativeFile.h"
#include <deque>
#include <map>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <optional>
namespace hm {
namespace {
struct Hint {std::wstring Path;UINT Size;std::string Stamp;Json Grant;};
/// <summary>所有磁盘、对端核验和管道IO只在专用线程执行；队列64项、版本去重128项，缺服务后30秒不再探测。</summary>
class Hints {
    std::mutex Gate;std::condition_variable Changed;std::deque<Hint> Waiting;std::map<std::string,ULONGLONG> Recent;
    Handle Stopping{CreateEventW(nullptr,TRUE,FALSE,nullptr)};bool Closed{},Active{};ULONGLONG AbsentUntil{},Submitted{},Sent{},Probes{},Suppressed{},Rejected{};std::thread Worker;
    std::wstring Pipe,Peer;
    static bool Enabled(){
#ifdef HYBRIDMOUNT_NATIVE_TESTS
        wchar_t pipe[256]{};auto size=GetEnvironmentVariableW(L"HYBRIDMOUNT_TEST_THUMBNAIL_PIPE",pipe,256);return size>0&&size<256&&std::wstring(pipe).starts_with(L"HybridMount.ThumbnailHints.Tests.");
#else
        return true;
#endif
    }
    void Names(){
#ifdef HYBRIDMOUNT_NATIVE_TESTS
        wchar_t pipe[256]{},peer[32768]{};auto pipeSize=GetEnvironmentVariableW(L"HYBRIDMOUNT_TEST_THUMBNAIL_PIPE",pipe,256);auto peerSize=GetEnvironmentVariableW(L"HYBRIDMOUNT_TEST_THUMBNAIL_PEER",peer,32768);
        Require(pipeSize>0&&pipeSize<256&&std::wstring(pipe).starts_with(L"HybridMount.ThumbnailHints.Tests.")&&std::wstring(pipe).find(L'\\')==std::wstring::npos&&peerSize>0&&peerSize<32768,E_ACCESSDENIED);Pipe=pipe;Peer=FullPath(peer);
#else
        DWORD session{};WinCheck(ProcessIdToSessionId(GetCurrentProcessId(),&session));Pipe=ProductScope()+L".v2."+CurrentSid()+L"."+std::to_wstring(session);Peer=ModuleDirectory()+L"\\HybridMount.exe";
#endif
    }
    /// <summary>取消后仍观察真正IO完成，保持OVERLAPPED和缓冲存活；匿名缓存读取不经过本方法。</summary>
    void Transfer(HANDLE pipe,void* memory,DWORD length,bool write,ULONGLONG deadline){
        auto bytes=static_cast<BYTE*>(memory);
        while(length){
            Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));WinCheck(event.Value!=nullptr);OVERLAPPED io{};io.hEvent=event.Value;DWORD done{};
            if(!(write?WriteFile(pipe,bytes,length,&done,&io):ReadFile(pipe,bytes,length,&done,&io))){
                auto error=GetLastError();Require(error==ERROR_IO_PENDING,HRESULT_FROM_WIN32(error));HANDLE waits[]{Stopping.Value,event.Value};auto now=GetTickCount64();auto selected=WaitForMultipleObjects(2,waits,FALSE,now>=deadline?0:static_cast<DWORD>(deadline-now));
                if(selected!=WAIT_OBJECT_0+1){CancelIoEx(pipe,&io);GetOverlappedResult(pipe,&io,&done,TRUE);throw Failure(HRESULT_FROM_WIN32(selected==WAIT_OBJECT_0?ERROR_OPERATION_ABORTED:ERROR_TIMEOUT));}
                WinCheck(GetOverlappedResult(pipe,&io,&done,FALSE));
            }
            Require(done>0,HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE));bytes+=done;length-=done;
        }
    }
    void CheckPeer(HANDLE pipe){
        ULONG id{};WinCheck(GetNamedPipeServerProcessId(pipe,&id));DWORD ownSession{},peerSession{};WinCheck(ProcessIdToSessionId(GetCurrentProcessId(),&ownSession));WinCheck(ProcessIdToSessionId(id,&peerSession));Require(ownSession==peerSession,E_ACCESSDENIED);
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,id));WinCheck(process.Value!=nullptr);std::wstring image(32768,L'\0');DWORD length=32768;WinCheck(QueryFullProcessImageNameW(process.Value,0,image.data(),&length));image.resize(length);Require(SamePath(FullPath(image),FullPath(Peer)),E_ACCESSDENIED);
    }
    void Send(Hint hint){
        Require(ReadBackgroundThumbnailGrant(hint.Path,Stopping.Value)==hint.Grant&&LocalThumbnailStamp(hint.Path)==hint.Stamp,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        {auto file=OpenAttributes(hint.Path);auto state=InspectFile(file.Value);auto cloud=InspectPlaceholder(file.Value);Require(!state.Directory&&state.Length>0&&cloud.InSync==CF_IN_SYNC_STATE_IN_SYNC&&cloud.Modified==0&&!HasFullContent(file.Value,state.Length),E_ACCESSDENIED);}
        {std::lock_guard lock(Gate);++Probes;}
        Handle pipe(CreateFileW((L"\\\\.\\pipe\\"+Pipe).c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr));
        if(pipe.Value==INVALID_HANDLE_VALUE){auto error=GetLastError();if(error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND){std::lock_guard lock(Gate);AbsentUntil=GetTickCount64()+30000;}throw Failure(HRESULT_FROM_WIN32(error));}
        CheckPeer(pipe.Value);GUID id{};Check(CoCreateGuid(&id));auto requestId=Hex(&id,sizeof(id));auto data=Json{{"version",2},{"requestId",requestId},{"operation","requestThumbnail"},{"paths",Json::array({Utf8(hint.Path)})},{"previewSize",hint.Size}}.dump();Require(data.size()<=256*1024);
        auto deadline=GetTickCount64()+500;auto length=static_cast<DWORD>(data.size());Transfer(pipe.Value,&length,4,true,deadline);Transfer(pipe.Value,data.data(),length,true,deadline);
        Transfer(pipe.Value,&length,4,false,deadline);Require(length>0&&length<=1024*1024);std::string response(length,'\0');Transfer(pipe.Value,response.data(),length,false,deadline);auto value=Parse(response,1024*1024);Require(value.at("version")==2&&value.at("requestId")==requestId);std::lock_guard lock(Gate);++Sent;
    }
    void Run()noexcept{
        try{Names();}catch(...){std::lock_guard lock(Gate);Closed=true;return;}
        while(true){
            std::optional<Hint> hint;
            {std::unique_lock lock(Gate);Changed.wait(lock,[&]{return Closed||!Waiting.empty();});if(Closed)break;if(GetTickCount64()<AbsentUntil){Suppressed+=Waiting.size();Waiting.clear();continue;}hint=std::move(Waiting.front());Waiting.pop_front();Active=true;}
            try{Send(std::move(*hint));}catch(...){std::lock_guard lock(Gate);++Rejected;}
            {std::lock_guard lock(Gate);Active=false;}
        }
    }
public:
    static bool Available(){return Enabled();}
    Hints(){WinCheck(Stopping.Value!=nullptr);Worker=std::thread([this]{Run();});}
    ~Hints(){Stop();}
    void Queue(const std::wstring& path,UINT size,const std::string& stamp,const Json& grant){
        const auto key=Sha256(stamp+"|"+grant.dump());const auto now=GetTickCount64();std::unique_lock lock(Gate,std::try_to_lock);if(!lock.owns_lock()||Closed)return;
        if(now<AbsentUntil){++Suppressed;return;}for(auto item=Recent.begin();item!=Recent.end();)if(item->second<=now)item=Recent.erase(item);else ++item;
        if(Recent.contains(key))return;if(Waiting.size()>=64){++Rejected;return;}while(Recent.size()>=128)Recent.erase(std::min_element(Recent.begin(),Recent.end(),[](const auto& a,const auto& b){return a.second<b.second;}));
        Recent[key]=now+15000;Waiting.push_back({path,size,stamp,grant});++Submitted;Changed.notify_one();
    }
    bool Busy(){std::unique_lock lock(Gate,std::try_to_lock);return !lock.owns_lock()||Active||!Waiting.empty();}
    Json Statistics(){std::unique_lock lock(Gate,std::try_to_lock);if(!lock.owns_lock())return {{"busy",true}};return {{"pending",Waiting.size()},{"submitted",Submitted},{"sent",Sent},{"probes",Probes},{"suppressed",Suppressed},{"rejected",Rejected},{"absentUntil",AbsentUntil}};}
    void Stop()noexcept{try{{std::lock_guard lock(Gate);Closed=true;Waiting.clear();SetEvent(Stopping.Value);Changed.notify_all();}if(Worker.joinable())Worker.join();}catch(...) {}}
};
std::mutex FactoryGate;std::unique_ptr<Hints> Current;bool Closed{};
}
void QueueRemoteThumbnailHint(const std::wstring& path,UINT size,const std::string& stamp,const Json& grant)noexcept{try{if(!Hints::Available())return;std::unique_lock lock(FactoryGate,std::try_to_lock);if(!lock.owns_lock()||Closed)return;if(!Current)Current=std::make_unique<Hints>();Current->Queue(path,size,stamp,grant);}catch(...) {}}
bool RemoteThumbnailHintsPending()noexcept{try{std::unique_lock lock(FactoryGate,std::try_to_lock);return !lock.owns_lock()||(Current&&Current->Busy());}catch(...){return false;}}
Json RemoteThumbnailHintStatistics(){std::unique_lock lock(FactoryGate,std::try_to_lock);return lock.owns_lock()?(Current?Current->Statistics():Json::object()):Json{{"busy",true}};}
void StopRemoteThumbnailHints()noexcept{try{std::unique_ptr<Hints> current;{std::lock_guard lock(FactoryGate);Closed=true;current=std::move(Current);}if(current)current->Stop();}catch(...) {}}
}

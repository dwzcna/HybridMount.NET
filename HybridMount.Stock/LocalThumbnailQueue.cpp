#include "LocalThumbnailQueue.h"
#include "LocalThumbnail.h"
#include <shellapi.h>
#include <deque>
#include <map>
#include <set>
#include <mutex>
#include <condition_variable>
#include <algorithm>
#include <optional>

namespace hm {
namespace {
/// <summary>原生结果只驻留此宿主；所有容器由Gate保护，文件验证及解码始终在锁外进行。</summary>
class LocalQueue {
    struct Work {std::wstring Path;std::string Stamp,Key,FileKey;Json Grant;ULONGLONG Submitted{};};
    struct Result {std::vector<BYTE> Png;ULONGLONG Until{},Used{};};
    struct Notice {std::wstring Path;std::string Stamp;Json Grant;ULONGLONG Due{};};
    std::mutex Gate;std::condition_variable Changed;std::deque<Work> Waiting;std::set<std::string> Pending,PendingFiles;std::map<std::string,Work> Deferred;
    std::map<std::string,Result> Results;std::map<std::string,ULONGLONG> Failed;std::map<std::wstring,Notice> Notices;
    Handle Stopping{CreateEventW(nullptr,TRUE,FALSE,nullptr)};bool Closed{},Active{};size_t Bytes{};
    ULONGLONG Submitted{},Decoded{},Failures{},Notified{},Hits{},Rejected{},QueueMilliseconds{},DecodeMilliseconds{},NotifyMilliseconds{},NextNotice{},Started{},StartupMilliseconds{},WorkerDecodeMilliseconds{},PublishMilliseconds{};
    HRESULT LastError=S_OK;std::string LastStage;std::thread Worker;
    static std::string Key(const std::string& stamp,const Json& grant){return Sha256(stamp+"|"+grant.dump());}
    void Trim(ULONGLONG now){
        for(auto i=Results.begin();i!=Results.end();)if(i->second.Until<=now){Bytes-=i->second.Png.size();i=Results.erase(i);}else ++i;
        while(Results.size()>128||Bytes>32ULL*1024*1024){auto oldest=std::min_element(Results.begin(),Results.end(),[](const auto& a,const auto& b){return a.second.Used<b.second.Used;});Bytes-=oldest->second.Png.size();Results.erase(oldest);}
        for(auto i=Failed.begin();i!=Failed.end();)if(i->second<=now)i=Failed.erase(i);else ++i;
        while(Failed.size()>128)Failed.erase(std::min_element(Failed.begin(),Failed.end(),[](const auto& a,const auto& b){return a.second<b.second;}));
    }
    /// <summary>单文件通知前再次核验根授权及正文版本；不发目录刷新，不使用同步FLUSH。</summary>
    void Notify(Notice notice) noexcept {
        const auto started=GetTickCount64();bool sent=false;
        try{if(WaitForSingleObject(Stopping.Value,0)==WAIT_TIMEOUT&&ReadThumbnailGrant(notice.Path)==notice.Grant&&LocalThumbnailStamp(notice.Path)==notice.Stamp){SHChangeNotify(SHCNE_UPDATEITEM,SHCNF_PATHW|SHCNF_FLUSHNOWAIT,notice.Path.c_str(),nullptr);sent=true;}}catch(...){}
        std::lock_guard lock(Gate);NotifyMilliseconds+=GetTickCount64()-started;if(sent)++Notified;
    }
    /// <summary>工作线程单项解码；旧内容和关闭后的结果不能进入缓存或通知队列。</summary>
    void Decode(Work work) noexcept {
        const auto started=GetTickCount64();bool failed=false;std::vector<BYTE> png;std::string finalStamp;HRESULT errorCode=S_OK;std::string errorStage;LocalThumbnailTiming timing;
        try{
            Require(LocalThumbnailStamp(work.Path)==work.Stamp&&ReadThumbnailGrant(work.Path)==work.Grant,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
            // 缓存键必须取自解码回执；退出后再读到的新内容戳不能冒认旧图片。
            png=DecodeLocalThumbnail(work.Path,Stopping.Value,finalStamp,timing);
            Require(ReadThumbnailGrant(work.Path)==work.Grant,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        }catch(const Failure& error){failed=true;errorCode=error.Code;errorStage=error.Stage;}catch(...){failed=true;errorCode=E_FAIL;}
        std::optional<Work> latest;{std::lock_guard lock(Gate);const auto now=GetTickCount64();DecodeMilliseconds+=now-started;if(timing.Started)++Started;StartupMilliseconds+=timing.Startup;WorkerDecodeMilliseconds+=timing.Decode;QueueMilliseconds+=started-work.Submitted;Pending.erase(work.Key);PendingFiles.erase(work.FileKey);if(auto found=Deferred.find(work.FileKey);found!=Deferred.end()){latest=std::move(found->second);Deferred.erase(found);}Active=false;
        if(!Closed){
            if(failed||png.empty()){LastError=errorCode;LastStage=errorStage;++Failures;Failed[work.Key]=now+5*60*1000;}
            else{
                const auto publishing=GetTickCount64();++Decoded;auto key=Key(finalStamp,work.Grant);auto old=Results.find(key);if(old!=Results.end())Bytes-=old->second.Png.size();Bytes+=png.size();Results[key]={std::move(png),now+2*60*1000,now};
                if(Notices.size()<128||Notices.contains(work.Path))Notices[work.Path]={work.Path,finalStamp,work.Grant,now+250};PublishMilliseconds+=GetTickCount64()-publishing;
            }
            Trim(now);
        }
        Changed.notify_all();}
        // 在途版本变化只保留最后一次提示；只读关闭已被最终结果覆盖时直接复用，不再二次解码。
        if(latest)try{if(LocalThumbnailStamp(latest->Path)==latest->Stamp&&ReadThumbnailGrant(latest->Path)==latest->Grant)Read(latest->Path,latest->Stamp,latest->Grant);}catch(...){}
    }
    void Run() noexcept {
        const auto initialized=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
        while(true){
            std::unique_lock lock(Gate);if(Closed)break;const auto now=GetTickCount64();
            auto notice=std::find_if(Notices.begin(),Notices.end(),[&](const auto& row){return row.second.Due<=now&&NextNotice<=now;});
            if(notice!=Notices.end()){auto current=std::move(notice->second);Notices.erase(notice);NextNotice=now+125;Active=true;lock.unlock();Notify(std::move(current));lock.lock();Active=false;continue;}
            if(!Waiting.empty()){auto work=std::move(Waiting.front());Waiting.pop_front();Active=true;lock.unlock();Decode(std::move(work));continue;}
            Changed.wait_for(lock,std::chrono::milliseconds(25));
        }
        if(SUCCEEDED(initialized))CoUninitialize();
    }
public:
    LocalQueue():Worker([this]{Run();}){}
    ~LocalQueue(){Stop();}
    std::vector<BYTE> Read(const std::wstring& path,const std::string& stamp,const Json& grant){
        const auto now=GetTickCount64();const auto key=Key(stamp,grant);const auto fileKey=Key(stamp.substr(0,stamp.find(':')),grant);std::lock_guard lock(Gate);Trim(now);if(Closed)return {};
        if(auto found=Results.find(key);found!=Results.end()){found->second.Used=now;++Hits;return found->second.Png;}
        if(Failed.contains(key)||Pending.contains(key))return {};
        if(PendingFiles.contains(fileKey)){Deferred[fileKey]={path,stamp,key,fileKey,grant,now};return {};}
        if(Pending.size()>=64){++Rejected;return {};}
        Pending.insert(key);PendingFiles.insert(fileKey);Waiting.push_back({path,stamp,key,fileKey,grant,now});++Submitted;Changed.notify_one();return {};
    }
    bool Busy(){std::lock_guard lock(Gate);return Active||!Waiting.empty()||!Notices.empty();}
    Json Statistics(){std::lock_guard lock(Gate);return {{"pending",Pending.size()},{"cached",Results.size()},{"bytes",Bytes},{"submitted",Submitted},{"decoded",Decoded},{"failed",Failures},{"hits",Hits},{"rejected",Rejected},{"notified",Notified},{"queueMs",QueueMilliseconds},{"decodeMs",DecodeMilliseconds},{"notifyMs",NotifyMilliseconds},{"started",Started},{"startupMs",StartupMilliseconds},{"workerDecodeMs",WorkerDecodeMilliseconds},{"publishMs",PublishMilliseconds},{"lastError",LastError},{"lastStage",LastStage}};}
    void Stop(){
        {std::lock_guard lock(Gate);if(!Closed){Closed=true;SetEvent(Stopping.Value);Waiting.clear();Deferred.clear();Notices.clear();Changed.notify_all();}}
        if(Worker.joinable())Worker.join();
    }
};
std::mutex QueueGate;std::unique_ptr<LocalQueue> Queue;
}
std::vector<BYTE> QueueLocalThumbnail(const std::wstring& path,const std::string& stamp,const Json& grant){std::lock_guard lock(QueueGate);if(!Queue)Queue=std::make_unique<LocalQueue>();return Queue->Read(path,stamp,grant);}
BOOL WINAPI LocalThumbnailWorkPending(void* context) noexcept{try{if(context&&WaitForSingleObject(static_cast<HANDLE>(context),0)==WAIT_OBJECT_0){StopLocalThumbnails();return FALSE;}std::lock_guard lock(QueueGate);return Queue&&Queue->Busy();}catch(...){return FALSE;}}
Json LocalThumbnailStatistics(){std::lock_guard lock(QueueGate);return Queue?Queue->Statistics():Json::object();}
void StopLocalThumbnails() noexcept{try{std::unique_ptr<LocalQueue> current;{std::lock_guard lock(QueueGate);current=std::move(Queue);}if(current)current->Stop();}catch(...) {}}
}

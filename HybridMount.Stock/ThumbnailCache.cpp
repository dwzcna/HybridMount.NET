#include "ThumbnailCache.h"
#include "NativeFile.h"
#ifndef HYBRIDMOUNT_SHELL_EXTENSION
#include "LocalThumbnail.h"
#endif
#include "NativeDiagnostics.h"
#include <atomic>
#include <array>
#include <map>
#include <mutex>
#include <memory>
#include <algorithm>
#include <exception>

namespace hm {
thread_local const char* ThumbnailReadStage="start";
thread_local Json ThumbnailEvidence=Json::object();
namespace {
std::atomic<ULONGLONG> QueryCount{},QueryMilliseconds{},BusyCount{},GateBusyCount{},SqliteBusyCount{},CapacityBusyCount{},ConnectionCount{},ConnectionReuse{},ConnectionEvictions{};
std::array<std::atomic<unsigned long long>,8> StageMilliseconds{},StageCounts{};
/// <summary>只累计固定阶段数值；即使错误早退也记录真实耗时，查询诊断不访问磁盘或源。</summary>
struct StageTiming {
    size_t Stage{};ULONGLONG Started=GetTickCount64();
    void Next(size_t stage){StageMilliseconds[Stage]+=GetTickCount64()-Started;++StageCounts[Stage];Stage=stage;Started=GetTickCount64();}
    ~StageTiming(){StageMilliseconds[Stage]+=GetTickCount64()-Started;++StageCounts[Stage];}
};
struct QueryTiming {ULONGLONG Started=GetTickCount64();~QueryTiming(){++QueryCount;QueryMilliseconds+=GetTickCount64()-Started;}};
struct sqlite3;struct sqlite3_stmt;
void SqliteCheck(int code,ULONGLONG deadline=0){if(code==9&&deadline&&GetTickCount64()>=deadline){ThumbnailReadStage="sqlite-timeout";throw Failure(HRESULT_FROM_WIN32(ERROR_TIMEOUT));}if(code==5||code==6){ThumbnailReadStage="sqlite-busy";++BusyCount;++SqliteBusyCount;}if(code!=0)throw Failure(HRESULT_FROM_WIN32(code==5||code==6?ERROR_BUSY:code==11||code==26?ERROR_FILE_CORRUPT:ERROR_INVALID_DATA));}
void ReportThumbnailFailure(HRESULT code)noexcept{if(code==HRESULT_FROM_WIN32(ERROR_NOT_FOUND)||code==HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)||code==E_NOTIMPL||code==E_ACCESSDENIED)return;static std::atomic<ULONGLONG> last{};auto now=GetTickCount64(),previous=last.load();if(now-previous<60000||!last.compare_exchange_strong(previous,now))return;std::string type="Thumbnail.";auto stage=std::string(ThumbnailReadStage);if(stage.size()>64||stage.find_first_not_of("abcdefghijklmnopqrstuvwxyz-")!=std::string::npos)stage="read";type+=stage;RecordNativeFailure("ShellServer",code,type.c_str());}
/// <summary>只解析应用目录的现有SQLite C ABI；不搜索PATH，不创建第二写入者。</summary>
struct Sqlite {
    HMODULE Module{};
    int(__cdecl* Open)(const char*,sqlite3**,int,const char*){};int(__cdecl* Close)(sqlite3*){};
    int(__cdecl* Prepare)(sqlite3*,const char*,int,sqlite3_stmt**,const char**){};int(__cdecl* Step)(sqlite3_stmt*){};int(__cdecl* Finalize)(sqlite3_stmt*){};int(__cdecl* Reset)(sqlite3_stmt*){};int(__cdecl* Clear)(sqlite3_stmt*){};
    int(__cdecl* BindText)(sqlite3_stmt*,int,const char*,int,void(__cdecl*)(void*)){};int(__cdecl* BindInt)(sqlite3_stmt*,int,int){};
    const unsigned char*(__cdecl* Text)(sqlite3_stmt*,int){};const void*(__cdecl* Blob)(sqlite3_stmt*,int){};int(__cdecl* Bytes)(sqlite3_stmt*,int){};int(__cdecl* Int)(sqlite3_stmt*,int){};
    int(__cdecl* Exec)(sqlite3*,const char*,void*,void*,char**){};void(__cdecl* Progress)(sqlite3*,int,int(__cdecl*)(void*),void*){};
    template<class T> void Load(T& target,const char* name){auto proc=GetProcAddress(Module,name);Require(proc!=nullptr);static_assert(sizeof(target)==sizeof(proc));memcpy(&target,&proc,sizeof(target));}
    Sqlite(){
        Module=LoadLibraryExW((ModuleDirectory()+L"\\e_sqlite3.dll").c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);Require(Module!=nullptr,HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND));
        try{Load(Open,"sqlite3_open_v2");Load(Close,"sqlite3_close");Load(Prepare,"sqlite3_prepare_v2");Load(Step,"sqlite3_step");Load(Finalize,"sqlite3_finalize");Load(Reset,"sqlite3_reset");Load(Clear,"sqlite3_clear_bindings");Load(BindText,"sqlite3_bind_text");Load(BindInt,"sqlite3_bind_int");Load(Text,"sqlite3_column_text");Load(Blob,"sqlite3_column_blob");Load(Bytes,"sqlite3_column_bytes");Load(Int,"sqlite3_column_int");Load(Exec,"sqlite3_exec");Load(Progress,"sqlite3_progress_handler");}catch(...){FreeLibrary(Module);throw;}
    }
    ~Sqlite(){FreeLibrary(Module);}
};
/// <summary>一个挂载复用只读连接及有限SQL语句；每次请求单独BEGIN／ROLLBACK，绝不保留跨请求读事务。</summary>
struct Database {
    Sqlite& Api;sqlite3* Value{};ULONGLONG Deadline{};std::string Identity;std::map<std::string,sqlite3_stmt*> Statements;bool Healthy=true;
    Database(Sqlite& api,const std::wstring& path):Api(api){
        NoLinks(path,false);Identity=InspectFile(path).Identity;auto encoded=Utf8(path);const auto result=Api.Open(encoded.c_str(),&Value,1|0x10000,nullptr);
        if(result!=0){if(Value)Api.Close(Value);Value=nullptr;SqliteCheck(result);}
        auto setup=Api.Exec(Value,"PRAGMA query_only=ON;PRAGMA trusted_schema=OFF;PRAGMA busy_timeout=0",nullptr,nullptr,nullptr);
        if(setup!=0){Api.Close(Value);Value=nullptr;SqliteCheck(setup);}++ConnectionCount;
    }
    void Begin(){Deadline=GetTickCount64()+100;Api.Progress(Value,1000,[](void* state){return GetTickCount64()>=static_cast<Database*>(state)->Deadline?1:0;},this);SqliteCheck(Api.Exec(Value,"BEGIN",nullptr,nullptr,nullptr),Deadline);}
    void End()noexcept{Api.Progress(Value,0,nullptr,nullptr);if(Api.Exec(Value,"ROLLBACK",nullptr,nullptr,nullptr)!=0)Healthy=false;}
    ~Database(){for(auto& row:Statements)Api.Finalize(row.second);if(Value)Api.Close(Value);}
};
/// <summary>语句仅在此连接的非阻塞租约内借用；析构清除绑定和游标，后续请求不能看到旧行或持有BLOB指针。</summary>
struct Statement {
    Sqlite& Api;sqlite3_stmt* Value{};ULONGLONG Deadline;
    Statement(Database& db,const char* sql):Api(db.Api),Deadline(db.Deadline){
        auto found=db.Statements.find(sql);
        if(found==db.Statements.end()){
            Require(db.Statements.size()<8,HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA));auto code=Api.Prepare(db.Value,sql,-1,&Value,nullptr);
            if(code!=0){if(Value)Api.Finalize(Value);Value=nullptr;SqliteCheck(code,Deadline);}Require(Value!=nullptr);db.Statements.emplace(sql,Value);
        }else Value=found->second;
        Api.Reset(Value);Api.Clear(Value);
    }
    ~Statement(){if(Value){Api.Reset(Value);Api.Clear(Value);}}
    void Bind(int index,const std::string& text){Require(Api.BindText(Value,index,text.data(),static_cast<int>(text.size()),reinterpret_cast<void(__cdecl*)(void*)>(static_cast<intptr_t>(-1)))==0,E_FAIL);}
    void Bind(int index,int value){Require(Api.BindInt(Value,index,value)==0,E_FAIL);}
    bool Row(){auto code=Api.Step(Value);if(code!=100&&code!=101)SqliteCheck(code,Deadline);return code==100;}
    std::string Text(int column){auto count=Api.Bytes(Value,column);Require(count>=0&&count<=65536);auto bytes=Api.Text(Value,column);Require(bytes!=nullptr);return std::string(reinterpret_cast<const char*>(bytes),count);}
};
struct CacheContext {std::mutex Gate;std::atomic<unsigned> Background{};std::unique_ptr<Database> Db;ULONGLONG Used{};};
struct ContextCache;
std::atomic<ContextCache*> CurrentCache{};
/// <summary>最多16个挂载连接，空闲500ms关闭；回收回调持有模块引用，DLL卸载不等待持加载器锁的线程。</summary>
struct ContextCache {
    Sqlite Api;std::mutex Gate;std::map<std::wstring,std::shared_ptr<CacheContext>> Entries;
    Handle Wake{CreateEventW(nullptr,FALSE,FALSE,nullptr)},Drained{CreateEventW(nullptr,TRUE,TRUE,nullptr)};bool Running{},Closing{};
    struct ReaperState {ContextCache* Owner{};HMODULE Module{};Handle Signal;};
    ContextCache(){WinCheck(Wake.Value!=nullptr&&Drained.Value!=nullptr);CurrentCache.store(this);}
    ~ContextCache(){CurrentCache.store(nullptr);}
    void Trim(ULONGLONG now){for(auto item=Entries.begin();item!=Entries.end();)if(item->second.use_count()==1&&(Closing||now-item->second->Used>=500)){item=Entries.erase(item);++ConnectionEvictions;}else ++item;}
    static void CALLBACK Reap(PTP_CALLBACK_INSTANCE instance,void* raw)noexcept{
        std::unique_ptr<ReaperState> state(static_cast<ReaperState*>(raw));auto& owner=*state->Owner;
        while(true){
            {std::lock_guard lock(owner.Gate);owner.Trim(GetTickCount64());if(owner.Entries.empty()){owner.Running=false;break;}}
            WaitForSingleObject(owner.Wake.Value,250);
        }
        // Running=false后不再访问Owner；独立事件句柄允许EXE收尾与本回调返回并发。
        SetEvent(state->Signal.Value);FreeLibraryWhenCallbackReturns(instance,state->Module);
    }
    bool Schedule(){
        if(Running)return true;HMODULE module{};
        if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,reinterpret_cast<LPCWSTR>(&Reap),&module))return false;
        struct Pin{HMODULE Value;~Pin(){if(Value)FreeLibrary(Value);}} pin{module};auto state=std::make_unique<ReaperState>();state->Owner=this;state->Module=module;
        if(!DuplicateHandle(GetCurrentProcess(),Drained.Value,GetCurrentProcess(),&state->Signal.Value,0,FALSE,DUPLICATE_SAME_ACCESS))return false;
        Running=true;ResetEvent(Drained.Value);
        if(!TrySubmitThreadpoolCallback(Reap,state.get(),nullptr)){Running=false;SetEvent(Drained.Value);return false;}
        pin.Value=nullptr;state.release();return true;
    }
    std::shared_ptr<CacheContext> Find(const std::wstring& path){
        std::unique_lock lock(Gate,std::try_to_lock);if(!lock.owns_lock()){ThumbnailReadStage="context-gate-busy";++GateBusyCount;++BusyCount;throw Failure(HRESULT_FROM_WIN32(ERROR_BUSY));}
        Require(!Closing,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));const auto now=GetTickCount64();Trim(now);
        if(auto found=Entries.find(path);found!=Entries.end()){found->second->Used=now;return found->second;}
        if(Entries.size()>=16){
            auto oldest=Entries.end();for(auto item=Entries.begin();item!=Entries.end();++item)if(item->second.use_count()==1&&(oldest==Entries.end()||item->second->Used<oldest->second->Used))oldest=item;
            if(oldest==Entries.end()){ThumbnailReadStage="connection-capacity";++CapacityBusyCount;++BusyCount;throw Failure(HRESULT_FROM_WIN32(ERROR_BUSY));}Entries.erase(oldest);++ConnectionEvictions;
        }
        auto context=std::make_shared<CacheContext>();context->Db=std::make_unique<Database>(Api,path);context->Used=now;Entries.emplace(path,context);
        if(!Schedule())Entries.erase(path);return context;
    }
    /// <summary>仅EXE正常收尾调用；先拒绝新查询，随后等待已有请求及短期回收完成，不在DllMain等待。</summary>
    void Close(){
        {std::lock_guard lock(Gate);Closing=true;SetEvent(Wake.Value);}
        while(true){
            {std::lock_guard lock(Gate);if(!Running){Entries.clear();return;}ResetEvent(Drained.Value);}
            WaitForSingleObject(Drained.Value,INFINITE);
        }
    }
};
ContextCache& Cache(){static ContextCache value;return value;}
/// <summary>并行请求遇到同连接忙立即退让；每次先核验数据库文件身份，替换文件不能继续复用旧连接。</summary>
struct ReadLease {
    std::shared_ptr<CacheContext> Context;std::unique_lock<std::mutex> Lock;Database* Value{};bool Active{};int Exceptions=std::uncaught_exceptions();
    explicit ReadLease(const std::wstring& path,bool background=false):Context(Cache().Find(path)),Lock(Context->Gate,std::defer_lock){
        if(background){
            // 背景校验只申请短期优先权；新的COM查询立即退让，已有查询收尾后最多100ms取得连接。
            struct Waiter{std::atomic<unsigned>& Value;Waiter(std::atomic<unsigned>& value):Value(value){++Value;}~Waiter(){--Value;}} waiter(Context->Background);
            const auto until=GetTickCount64()+100;while(!Lock.try_lock()){if(GetTickCount64()>=until){ThumbnailReadStage="query-gate-timeout";++GateBusyCount;++BusyCount;throw Failure(HRESULT_FROM_WIN32(ERROR_BUSY));}Sleep(1);}
        }else if(Context->Background.load()!=0||!Lock.try_lock()){ThumbnailReadStage="query-gate-busy";++GateBusyCount;++BusyCount;throw Failure(HRESULT_FROM_WIN32(ERROR_BUSY));}
        try{
            NoLinks(path,false);const auto identity=InspectFile(path).Identity;
            if(!Context->Db||!Context->Db->Healthy||Context->Db->Identity!=identity)Context->Db=std::make_unique<Database>(Cache().Api,path);else ++ConnectionReuse;
            Value=Context->Db.get();Value->Begin();Active=true;
        }catch(...){Context->Db.reset();throw;}
    }
    ~ReadLease(){if(Active){Value->End();if(!Value->Healthy||std::uncaught_exceptions()>Exceptions)Context->Db.reset();}}
};
bool Valid(const Json& binding,const std::wstring& path){
    const auto& root=binding.at("Root");Require(root.at("Enabled").get<bool>()&&SamePath(FullPath(Text(binding,"Path")),FullPath(path)));ValidatePath(root,path);
    auto file=OpenAttributes(path);auto placeholder=InspectPlaceholder(file.Value);auto marker=Parse(std::string(placeholder.Identity.begin(),placeholder.Identity.end()),4096);
    Require(GuidN(marker.at("MountId"))==GuidN(root.at("MountId"))&&marker.at("EntryId")==binding.at("EntryId"));
    auto stamp=InspectFile(file.Value);Require(!stamp.Directory&&stamp.Identity==binding.at("FileId").get<std::string>()&&stamp.Length==Integer(binding,"Length")&&stamp.LastWrite==Integer(binding,"LastWriteTime"));
    const auto clean=binding.at("Clean").get<bool>();const auto usn=binding.value("Usn",0LL);
    Require(stamp.Key()==binding.at("Stamp").get<std::string>()||(clean&&usn>0&&stamp.Usn==usn));
    return clean?placeholder.InSync==CF_IN_SYNC_STATE_IN_SYNC&&placeholder.Modified==0:HasFullContent(file.Value,stamp.Length);
}
}
void StopThumbnailReadCache()noexcept{try{if(auto cache=CurrentCache.load())cache->Close();}catch(...) {}}
Json ReadThumbnailGrant(const std::wstring& path,bool background){Json grant;ReadThumbnailSnapshot(path,0,grant,background);return grant;}
std::vector<BYTE> ReadCachedThumbnail(const std::wstring& path,UINT size){Require(size>0);Json grant;return ReadThumbnailSnapshot(path,size,grant);}
std::vector<BYTE> ReadThumbnailSnapshot(const std::wstring& path,UINT size,Json& grant,bool background){
    QueryTiming timing;StageTiming phases;
    ThumbnailReadStage="root";Require(size<=256);auto canonical=FullPath(path);auto marker=RootMarker(canonical);const auto mount=GuidN(marker.at("MountId"));
    phases.Next(1);ThumbnailReadStage="database";ReadLease lease(PhysicalDataDirectory()+L"\\Mounting\\Thumbnails\\"+Wide(mount)+L".db",background);auto& db=*lease.Value;
    {Statement schema(db,"PRAGMA user_version");Require(schema.Row()&&schema.Api.Int(schema.Value,0)==1);}
    {Statement count(db,"SELECT count(*) FROM roots");Require(count.Row()&&count.Api.Int(count.Value,0)==1);}
    {Statement row(db,"SELECT json FROM roots WHERE mount=?1 AND length(json)<=65536 LIMIT 2");row.Bind(1,mount);Require(row.Row(),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));grant=Parse(row.Text(0));Require(!row.Row()&&grant.at("Enabled").get<bool>()&&GuidN(grant.at("MountId"))==mount&&Text(grant,"Owner")==CurrentSid(),E_ACCESSDENIED);ValidatePath(grant,canonical);}
    if(!size)return {};
    phases.Next(2);ThumbnailReadStage="binding";Json binding,root;std::string key;
    {Statement row(db,"SELECT b.json,b.content,r.json FROM bindings b JOIN roots r ON r.mount=b.mount WHERE b.path=?1 AND b.mount=?2 AND length(b.json)<=65536 AND length(r.json)<=65536 LIMIT 2");row.Bind(1,Utf8(canonical));row.Bind(2,mount);if(!row.Row())return {};binding=Parse(row.Text(0));key=row.Text(1);root=Parse(row.Text(2));Require(!row.Row());}
    phases.Next(3);ThumbnailReadStage="identity";Require(root==grant&&binding.at("Root")==root&&GuidN(root.at("MountId"))==mount&&Valid(binding,canonical));
    phases.Next(4);ThumbnailReadStage="content-key";const auto expected=Sha256(mount+"|"+root.at("Scope").get<std::string>()+"|"+std::to_string(Integer(root,"Epoch"))+"|"+binding.at("EntryId").get<std::string>()+"|"+binding.at("FileId").get<std::string>()+"|"+binding.at("Revision").get<std::string>()+"|v1");Require(expected==key);
    phases.Next(5);ThumbnailReadStage="image";Statement image(db,"SELECT png FROM images WHERE content=?1 AND size IN (32,64,128,256) AND length(png) BETWEEN 33 AND 393216 ORDER BY (size<?2),CASE WHEN size>=?2 THEN size ELSE -size END LIMIT 1");image.Bind(1,key);image.Bind(2,static_cast<int>(size));if(!image.Row())return {};
    const auto count=image.Api.Bytes(image.Value,0);const auto bytes=static_cast<const BYTE*>(image.Api.Blob(image.Value,0));Require(bytes&&count>=33&&count<=393216);const BYTE signature[]{137,80,78,71,13,10,26,10};Require(memcmp(bytes,signature,8)==0&&memcmp(bytes+12,"IHDR",4)==0);
    auto dimension=[](const BYTE* value){return (DWORD(value[0])<<24)|(DWORD(value[1])<<16)|(DWORD(value[2])<<8)|value[3];};auto width=dimension(bytes+16),height=dimension(bytes+20);Require(width>0&&height>0&&width<=256&&height<=256);
    if(GetTickCount64()>=db.Deadline){ThumbnailReadStage="sqlite-timeout";throw Failure(HRESULT_FROM_WIN32(ERROR_TIMEOUT));}phases.Next(6);ThumbnailReadStage="final-state";Require(Valid(binding,canonical));return {bytes,bytes+count};
}
Json ThumbnailQueryStatistics(){return {{"count",QueryCount.load()},{"milliseconds",QueryMilliseconds.load()},{"busy",BusyCount.load()},{"gateBusy",GateBusyCount.load()},{"sqliteBusy",SqliteBusyCount.load()},{"capacityBusy",CapacityBusyCount.load()},{"opened",ConnectionCount.load()},{"reused",ConnectionReuse.load()},{"evicted",ConnectionEvictions.load()},{"stages",{{"rootMs",StageMilliseconds[0].load()},{"queryMs",StageMilliseconds[1].load()},{"bindingMs",StageMilliseconds[2].load()},{"identityMs",StageMilliseconds[3].load()},{"keyMs",StageMilliseconds[4].load()},{"imageMs",StageMilliseconds[5].load()},{"revalidateMs",StageMilliseconds[6].load()},{"rootCount",StageCounts[0].load()},{"queryCount",StageCounts[1].load()}}}};}
HRESULT WINAPI ReadThumbnail(void*,const wchar_t* path,UINT size,BYTE** png,DWORD* length)noexcept{
    if(!png||!length)return E_POINTER;*png=nullptr;*length=0;if(!path)return E_INVALIDARG;
    try{
#ifdef HYBRIDMOUNT_SHELL_EXTENSION
        auto bytes=ReadCachedThumbnail(path,size);
#else
        auto bytes=ReadAvailableThumbnail(path,size);
#endif
        if(bytes.empty())return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);auto memory=static_cast<BYTE*>(CoTaskMemAlloc(bytes.size()));if(!memory)return E_OUTOFMEMORY;memcpy(memory,bytes.data(),bytes.size());*png=memory;*length=static_cast<DWORD>(bytes.size());return S_OK;}
    catch(const Failure& error){ReportThumbnailFailure(error.Code);return error.Code;}catch(const Json::exception&){ReportThumbnailFailure(HRESULT_FROM_WIN32(ERROR_INVALID_DATA));return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);}catch(...){ReportThumbnailFailure(E_FAIL);return E_FAIL;}
}
}

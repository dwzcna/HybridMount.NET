#include "ThumbnailCache.h"
#include "NativeFile.h"
#include "NativeDiagnostics.h"

namespace hm {
thread_local const char* ThumbnailReadStage="start";
thread_local ULONGLONG ThumbnailDeadline=0;
namespace {
constexpr ULONGLONG ThumbnailDeadlineMs=500;
void RecordThumbnailFailure(HRESULT code)noexcept{static volatile LONG64 last{};const auto now=static_cast<LONG64>(GetTickCount64());for(;;){const auto previous=InterlockedCompareExchange64(&last,0,0);if(previous!=0&&now-previous<1000)return;if(InterlockedCompareExchange64(&last,now,previous)==previous)break;}RecordNativeFailure("NativeRuntime",code,ThumbnailReadStage);}
struct sqlite3;struct sqlite3_stmt;
/// <summary>只解析应用目录的现有SQLite C ABI；不搜索PATH，不创建第二写入者。</summary>
struct Sqlite {
    HMODULE Module{};
    int(__cdecl* Open)(const char*,sqlite3**,int,const char*){};int(__cdecl* Close)(sqlite3*){};
    int(__cdecl* Prepare)(sqlite3*,const char*,int,sqlite3_stmt**,const char**){};int(__cdecl* Step)(sqlite3_stmt*){};int(__cdecl* Finalize)(sqlite3_stmt*){};
    int(__cdecl* BindText)(sqlite3_stmt*,int,const char*,int,void(__cdecl*)(void*)){};int(__cdecl* BindInt)(sqlite3_stmt*,int,int){};
    const unsigned char*(__cdecl* Text)(sqlite3_stmt*,int){};const void*(__cdecl* Blob)(sqlite3_stmt*,int){};int(__cdecl* Bytes)(sqlite3_stmt*,int){};int(__cdecl* Int)(sqlite3_stmt*,int){};
    int(__cdecl* Exec)(sqlite3*,const char*,void*,void*,char**){};void(__cdecl* Progress)(sqlite3*,int,int(__cdecl*)(void*),void*){};
    template<class T> void Load(T& target,const char* name){auto proc=GetProcAddress(Module,name);Require(proc!=nullptr);static_assert(sizeof(target)==sizeof(proc));memcpy(&target,&proc,sizeof(target));}
    Sqlite(){
        Module=LoadLibraryExW((ModuleDirectory()+L"\\e_sqlite3.dll").c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);Require(Module!=nullptr,HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND));
        try{Load(Open,"sqlite3_open_v2");Load(Close,"sqlite3_close");Load(Prepare,"sqlite3_prepare_v2");Load(Step,"sqlite3_step");Load(Finalize,"sqlite3_finalize");Load(BindText,"sqlite3_bind_text");Load(BindInt,"sqlite3_bind_int");Load(Text,"sqlite3_column_text");Load(Blob,"sqlite3_column_blob");Load(Bytes,"sqlite3_column_bytes");Load(Int,"sqlite3_column_int");Load(Exec,"sqlite3_exec");Load(Progress,"sqlite3_progress_handler");}catch(...){FreeLibrary(Module);throw;}
    }
    ~Sqlite(){FreeLibrary(Module);}
};
Sqlite& Library(){static Sqlite api;return api;}
struct Database {
    Sqlite& Api;sqlite3* Value{};ULONGLONG Deadline{};
    explicit Database(const std::wstring& path):Api(Library()){
        NoLinks(path,false);auto encoded=Utf8(path);const auto result=Api.Open(encoded.c_str(),&Value,1|0x10000,nullptr);
        if(result!=0){if(Value)Api.Close(Value);Value=nullptr;throw Failure(E_FAIL);}Deadline=GetTickCount64()+ThumbnailDeadlineMs;
        ThumbnailDeadline=Deadline;Api.Progress(Value,1000,[](void* state){return GetTickCount64()>=static_cast<Database*>(state)->Deadline?1:0;},this);
        if(Api.Exec(Value,"PRAGMA query_only=ON;PRAGMA trusted_schema=OFF;PRAGMA busy_timeout=0;BEGIN",nullptr,nullptr,nullptr)!=0){Api.Close(Value);Value=nullptr;throw Failure(E_FAIL);}
    }
    ~Database(){if(Value){Api.Exec(Value,"ROLLBACK",nullptr,nullptr,nullptr);Api.Close(Value);}}
};
struct Statement {
    Sqlite& Api;sqlite3_stmt* Value{};
    Statement(Database& db,const char* sql):Api(db.Api){Require(Api.Prepare(db.Value,sql,-1,&Value,nullptr)==0,E_FAIL);}
    ~Statement(){Api.Finalize(Value);}
    void Bind(int index,const std::string& text){Require(Api.BindText(Value,index,text.data(),static_cast<int>(text.size()),reinterpret_cast<void(__cdecl*)(void*)>(static_cast<intptr_t>(-1)))==0,E_FAIL);}
    void Bind(int index,int value){Require(Api.BindInt(Value,index,value)==0,E_FAIL);}
    bool Row(){auto code=Api.Step(Value);if(code!=100&&code!=101){if(ThumbnailDeadline&&GetTickCount64()>=ThumbnailDeadline){ThumbnailReadStage="timeout";throw Failure(HRESULT_FROM_WIN32(ERROR_TIMEOUT));}throw Failure(E_FAIL);}return code==100;}
    std::string Text(int column){auto count=Api.Bytes(Value,column);Require(count>=0&&count<=65536);auto bytes=Api.Text(Value,column);Require(bytes!=nullptr);return std::string(reinterpret_cast<const char*>(bytes),count);}
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
std::vector<BYTE> ReadCachedThumbnail(const std::wstring& path,UINT size){
    ThumbnailReadStage="root";Require(size>0&&size<=256);auto canonical=FullPath(path);auto marker=RootMarker(canonical);const auto mount=GuidN(marker.at("MountId"));
    ThumbnailReadStage="database";auto database=PhysicalDataDirectory()+L"\\Mounting\\Thumbnails\\"+Wide(mount)+L".db";if(GetFileAttributesW(database.c_str())==INVALID_FILE_ATTRIBUTES){const auto error=GetLastError();if(error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND){ThumbnailReadStage="db-missing";return {};}}
    std::unique_ptr<Database> db;try{db=std::make_unique<Database>(database);}catch(...){ThumbnailReadStage="sqlite-load-failure";throw;}
    {ThumbnailReadStage="schema";Statement schema(*db,"PRAGMA user_version");Require(schema.Row()&&schema.Api.Int(schema.Value,0)==1);}
    {Statement count(*db,"SELECT count(*) FROM roots");Require(count.Row()&&count.Api.Int(count.Value,0)==1);}
    ThumbnailReadStage="binding";Json binding,root;std::string key;
    {Statement row(*db,"SELECT b.json,b.content,r.json FROM bindings b JOIN roots r ON r.mount=b.mount WHERE b.path=?1 AND b.mount=?2 AND length(b.json)<=65536 AND length(r.json)<=65536 LIMIT 2");row.Bind(1,Utf8(canonical));row.Bind(2,mount);if(!row.Row())return {};binding=Parse(row.Text(0));key=row.Text(1);root=Parse(row.Text(2));Require(!row.Row());}
    ThumbnailReadStage="identity-mismatch";Require(binding.at("Root")==root&&GuidN(root.at("MountId"))==mount&&Valid(binding,canonical));
    ThumbnailReadStage="content-key";const auto expected=Sha256(mount+"|"+root.at("Scope").get<std::string>()+"|"+std::to_string(Integer(root,"Epoch"))+"|"+binding.at("EntryId").get<std::string>()+"|"+binding.at("FileId").get<std::string>()+"|"+binding.at("Revision").get<std::string>()+"|v1");Require(expected==key);
    ThumbnailReadStage="image";Statement image(*db,"SELECT png FROM images WHERE content=?1 AND size IN (32,64,128,256) AND length(png) BETWEEN 33 AND 393216 ORDER BY (size<?2),CASE WHEN size>=?2 THEN size ELSE -size END LIMIT 1");image.Bind(1,key);image.Bind(2,static_cast<int>(size));if(!image.Row())return {};
    ThumbnailReadStage="bad-png";const auto count=image.Api.Bytes(image.Value,0);const auto bytes=static_cast<const BYTE*>(image.Api.Blob(image.Value,0));Require(bytes&&count>=33&&count<=393216);const BYTE signature[]{137,80,78,71,13,10,26,10};Require(memcmp(bytes,signature,8)==0&&memcmp(bytes+12,"IHDR",4)==0);
    auto dimension=[](const BYTE* value){return (DWORD(value[0])<<24)|(DWORD(value[1])<<16)|(DWORD(value[2])<<8)|value[3];};auto width=dimension(bytes+16),height=dimension(bytes+20);Require(width>0&&height>0&&width<=256&&height<=256);
    if(GetTickCount64()>=db->Deadline){ThumbnailReadStage="timeout";throw Failure(HRESULT_FROM_WIN32(ERROR_TIMEOUT));}ThumbnailReadStage="stale-stamp";Require(Valid(binding,canonical));return {bytes,bytes+count};
}
HRESULT WINAPI ReadThumbnail(void*,const wchar_t* path,UINT size,BYTE** png,DWORD* length)noexcept{
    if(!png||!length)return E_POINTER;*png=nullptr;*length=0;if(!path)return E_INVALIDARG;
    try{auto bytes=ReadCachedThumbnail(path,size);if(bytes.empty())return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);auto memory=static_cast<BYTE*>(CoTaskMemAlloc(bytes.size()));if(!memory)return E_OUTOFMEMORY;memcpy(memory,bytes.data(),bytes.size());*png=memory;*length=static_cast<DWORD>(bytes.size());return S_OK;}
    catch(const Failure& error){RecordThumbnailFailure(error.Code);return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);}catch(...){RecordThumbnailFailure(E_FAIL);return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);}
}
}

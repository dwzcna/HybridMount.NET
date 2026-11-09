#include "ThumbnailCache.h"
#include "NativeFile.h"
#include "LocalThumbnail.h"
#include "NativeDiagnostics.h"
#include <atomic>

namespace hm {
thread_local const char* ThumbnailReadStage="start";
thread_local Json ThumbnailEvidence=Json::object();
namespace {
struct sqlite3;struct sqlite3_stmt;
void SqliteCheck(int code){if(code!=0)throw Failure(HRESULT_FROM_WIN32(code==5||code==6?ERROR_BUSY:code==11||code==26?ERROR_FILE_CORRUPT:ERROR_INVALID_DATA));}
void ReportThumbnailFailure(HRESULT code)noexcept{if(code==HRESULT_FROM_WIN32(ERROR_NOT_FOUND)||code==HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)||code==E_NOTIMPL||code==E_ACCESSDENIED)return;static std::atomic<ULONGLONG> last{};auto now=GetTickCount64(),previous=last.load();if(now-previous<60000||!last.compare_exchange_strong(previous,now))return;std::string type="Thumbnail.";auto stage=std::string(ThumbnailReadStage);if(stage.size()>64||stage.find_first_not_of("abcdefghijklmnopqrstuvwxyz-")!=std::string::npos)stage="read";type+=stage;RecordNativeFailure("ShellServer",code,type.c_str());}
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
    Sqlite& Api;sqlite3* Value{};ULONGLONG Deadline=GetTickCount64()+100;
    explicit Database(const std::wstring& path):Api(Library()){
        NoLinks(path,false);auto encoded=Utf8(path);const auto result=Api.Open(encoded.c_str(),&Value,1|0x10000,nullptr);
        if(result!=0){if(Value)Api.Close(Value);Value=nullptr;SqliteCheck(result);}
        Api.Progress(Value,1000,[](void* state){return GetTickCount64()>=static_cast<Database*>(state)->Deadline?1:0;},this);
        auto setup=Api.Exec(Value,"PRAGMA query_only=ON;PRAGMA trusted_schema=OFF;PRAGMA busy_timeout=0;BEGIN",nullptr,nullptr,nullptr);if(setup!=0){Api.Close(Value);Value=nullptr;SqliteCheck(setup);}
    }
    ~Database(){if(Value){Api.Exec(Value,"ROLLBACK",nullptr,nullptr,nullptr);Api.Close(Value);}}
};
struct Statement {
    Sqlite& Api;sqlite3_stmt* Value{};
    Statement(Database& db,const char* sql):Api(db.Api){SqliteCheck(Api.Prepare(db.Value,sql,-1,&Value,nullptr));}
    ~Statement(){Api.Finalize(Value);}
    void Bind(int index,const std::string& text){Require(Api.BindText(Value,index,text.data(),static_cast<int>(text.size()),reinterpret_cast<void(__cdecl*)(void*)>(static_cast<intptr_t>(-1)))==0,E_FAIL);}
    void Bind(int index,int value){Require(Api.BindInt(Value,index,value)==0,E_FAIL);}
    bool Row(){auto code=Api.Step(Value);if(code!=100&&code!=101)SqliteCheck(code);return code==100;}
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
Json ReadThumbnailGrant(const std::wstring& path) {
    auto canonical=FullPath(path);auto marker=RootMarker(canonical);auto mount=GuidN(marker.at("MountId"));Database db(PhysicalDataDirectory()+L"\\Mounting\\Thumbnails\\"+Wide(mount)+L".db");
    {Statement schema(db,"PRAGMA user_version");Require(schema.Row()&&schema.Api.Int(schema.Value,0)==1);}
    Statement row(db,"SELECT json FROM roots WHERE mount=?1 AND length(json)<=65536 LIMIT 2");row.Bind(1,mount);Require(row.Row(),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));auto root=Parse(row.Text(0));Require(!row.Row()&&root.at("Enabled").get<bool>()&&GuidN(root.at("MountId"))==mount&&Text(root,"Owner")==CurrentSid(),E_ACCESSDENIED);ValidatePath(root,canonical);return root;
}
std::vector<BYTE> ReadCachedThumbnail(const std::wstring& path,UINT size){
    ThumbnailReadStage="root";Require(size>0&&size<=256);auto canonical=FullPath(path);auto marker=RootMarker(canonical);const auto mount=GuidN(marker.at("MountId"));
    ThumbnailReadStage="database";Database db(PhysicalDataDirectory()+L"\\Mounting\\Thumbnails\\"+Wide(mount)+L".db");
    {Statement schema(db,"PRAGMA user_version");Require(schema.Row()&&schema.Api.Int(schema.Value,0)==1);}
    {Statement count(db,"SELECT count(*) FROM roots");Require(count.Row()&&count.Api.Int(count.Value,0)==1);}
    ThumbnailReadStage="binding";Json binding,root;std::string key;
    {Statement row(db,"SELECT b.json,b.content,r.json FROM bindings b JOIN roots r ON r.mount=b.mount WHERE b.path=?1 AND b.mount=?2 AND length(b.json)<=65536 AND length(r.json)<=65536 LIMIT 2");row.Bind(1,Utf8(canonical));row.Bind(2,mount);if(!row.Row())return {};binding=Parse(row.Text(0));key=row.Text(1);root=Parse(row.Text(2));Require(!row.Row());}
    ThumbnailReadStage="identity";Require(binding.at("Root")==root&&GuidN(root.at("MountId"))==mount&&Valid(binding,canonical));
    ThumbnailReadStage="content-key";const auto expected=Sha256(mount+"|"+root.at("Scope").get<std::string>()+"|"+std::to_string(Integer(root,"Epoch"))+"|"+binding.at("EntryId").get<std::string>()+"|"+binding.at("FileId").get<std::string>()+"|"+binding.at("Revision").get<std::string>()+"|v1");Require(expected==key);
    ThumbnailReadStage="image";Statement image(db,"SELECT png FROM images WHERE content=?1 AND size IN (32,64,128,256) AND length(png) BETWEEN 33 AND 393216 ORDER BY (size<?2),CASE WHEN size>=?2 THEN size ELSE -size END LIMIT 1");image.Bind(1,key);image.Bind(2,static_cast<int>(size));if(!image.Row())return {};
    const auto count=image.Api.Bytes(image.Value,0);const auto bytes=static_cast<const BYTE*>(image.Api.Blob(image.Value,0));Require(bytes&&count>=33&&count<=393216);const BYTE signature[]{137,80,78,71,13,10,26,10};Require(memcmp(bytes,signature,8)==0&&memcmp(bytes+12,"IHDR",4)==0);
    auto dimension=[](const BYTE* value){return (DWORD(value[0])<<24)|(DWORD(value[1])<<16)|(DWORD(value[2])<<8)|value[3];};auto width=dimension(bytes+16),height=dimension(bytes+20);Require(width>0&&height>0&&width<=256&&height<=256);
    ThumbnailReadStage="final-state";Require(GetTickCount64()<db.Deadline&&Valid(binding,canonical));return {bytes,bytes+count};
}
HRESULT WINAPI ReadThumbnail(void*,const wchar_t* path,UINT size,BYTE** png,DWORD* length)noexcept{
    if(!png||!length)return E_POINTER;*png=nullptr;*length=0;if(!path)return E_INVALIDARG;
    try{auto bytes=ReadAvailableThumbnail(path,size);if(bytes.empty())return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);auto memory=static_cast<BYTE*>(CoTaskMemAlloc(bytes.size()));if(!memory)return E_OUTOFMEMORY;memcpy(memory,bytes.data(),bytes.size());*png=memory;*length=static_cast<DWORD>(bytes.size());return S_OK;}
    catch(const Failure& error){ReportThumbnailFailure(error.Code);return error.Code;}catch(const Json::exception&){ReportThumbnailFailure(HRESULT_FROM_WIN32(ERROR_INVALID_DATA));return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);}catch(...){ReportThumbnailFailure(E_FAIL);return E_FAIL;}
}
}

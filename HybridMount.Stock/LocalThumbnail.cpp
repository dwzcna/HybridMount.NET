#include "LocalThumbnail.h"
#include "NativeFile.h"
#include "NativeWire.h"
#include "NativeDiagnostics.h"
#include <objidl.h>
#include <wrl.h>
#include <mutex>
#include <map>
#include <optional>
namespace hm {
namespace {
using Microsoft::WRL::ComPtr;
thread_local std::string LocalFailureStage;
/// <summary>其他进程看到的标签可能被过滤；持久占位信息才是冷文件判据，未知失败必须关闭读取。</summary>
std::optional<Placeholder> Persisted(HANDLE file) {
    try{return InspectPlaceholder(file);}catch(const Failure& error){if(error.Code==HRESULT_FROM_WIN32(ERROR_NOT_A_CLOUD_FILE)||error.Code==static_cast<HRESULT>(0x80070182)||error.Code==E_INVALIDARG)return {};throw;}
}
std::string ContentStamp(const FileState& stamp){return stamp.Identity+":"+std::to_string(stamp.Usn)+":"+std::to_string(stamp.Length)+":"+std::to_string(stamp.LastWrite)+":"+std::to_string(stamp.Usn?0:stamp.Change);}
/// <summary>本地保护引用始终留在解码进程；普通文件用共享读取，云文件核验完整后持有oplock。</summary>
struct Content {
    Handle Ordinary;HANDLE Protected{},Raw{};bool Referenced{};FileState Before;std::mutex Gate;int Requests{};long long Bytes{};std::map<long long,std::vector<BYTE>> Windows;const char* ReadStage{};Json Evidence=Json::object();std::optional<Placeholder> Cloud;
    explicit Content(const std::wstring& path,const Json& grant) {
        ValidatePath(grant,path);auto probe=OpenAttributes(path);auto stamp=InspectFile(probe.Value);Require(!stamp.Directory&&stamp.Links==1,E_ACCESSDENIED);
        FILE_ATTRIBUTE_TAG_INFO tag{};WinCheck(GetFileInformationByHandleEx(probe.Value,FileAttributeTagInfo,&tag,sizeof(tag)));
        const auto persisted=Persisted(probe.Value);const auto cloud=persisted.has_value();
        if(cloud) {
            auto info=InspectPlaceholder(probe.Value);auto marker=Parse(std::string(info.Identity.begin(),info.Identity.end()),4096);Require(GuidN(marker.at("MountId"))==GuidN(grant.at("MountId")),E_ACCESSDENIED);
            Require(HasFullContent(probe.Value,stamp.Length),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));
            Check(CfOpenFileWithOplock(path.c_str(),CF_OPEN_FILE_FLAG_EXCLUSIVE,&Protected));
            Referenced=CfReferenceProtectedHandle(Protected)!=FALSE;if(!Referenced){CfCloseHandle(Protected);Protected=nullptr;throw Failure(HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION));}
            Raw=CfGetWin32HandleFromProtectedHandle(Protected);
        } else {
            Require((tag.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)==0,E_ACCESSDENIED);
            Ordinary.Value=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_OVERLAPPED,nullptr);WinCheck(Ordinary.Value!=INVALID_HANDLE_VALUE);Raw=Ordinary.Value;
        }
        try{Before=InspectFile(Raw);if(ContentStamp(Before)!=ContentStamp(stamp))throw Failure(HRESULT_FROM_WIN32(ERROR_FILE_INVALID),"local-open-stamp");if(cloud){Require(HasFullContent(Raw,Before.Length),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));Cloud=InspectPlaceholder(Raw);}}
        catch(...){Close();throw;}
    }
    /// <summary>保护引用阻止外部正文变更；读取可推进访问元数据USN，只核验受保护正文及真实CFAPI状态。</summary>
    FileState Verify() {
        auto current=InspectFile(Raw);Require(current.Identity==Before.Identity&&current.Length==Before.Length&&current.LastWrite==Before.LastWrite&&!current.Directory&&current.Links==Before.Links,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        if(Cloud){Require(Referenced,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));auto info=InspectPlaceholder(Raw);Require(info.Identity==Cloud->Identity&&info.InSync==Cloud->InSync&&info.Pin==Cloud->Pin&&info.Modified==Cloud->Modified&&info.OnDisk==Cloud->OnDisk&&HasFullContent(Raw,current.Length),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));}
        return current;
    }
    void Close(){if(Referenced){CfReleaseProtectedHandle(Protected);Referenced=false;}if(Protected){CfCloseHandle(Protected);Protected=nullptr;}}
    ~Content(){Close();}
};
/// <summary>本地有界随机读取；不接受URL、不借用Shell项目内容流，所有Clone共用128MiB和512次预算。</summary>
class LocalStream final:public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,IStream> {
    std::shared_ptr<Content> _content;long long _position{};
public:
    explicit LocalStream(std::shared_ptr<Content> content,long long position=0):_content(std::move(content)),_position(position){}
    IFACEMETHODIMP Read(void* buffer,ULONG count,ULONG* read)override {
        if(read)*read=0;if(!buffer&&count)return STG_E_INVALIDPOINTER;
        try {
            std::lock_guard lock(_content->Gate);auto take=static_cast<DWORD>(std::min<long long>(count,_content->Before.Length-_position));if(!take)return count?S_FALSE:S_OK;
            _content->ReadStage="local-stream-before";_content->Verify();DWORD copied{};
            while(copied<take) {
                auto position=_position+copied;auto offset=position/(256*1024)*(256*1024);auto found=_content->Windows.find(offset);
                if(found==_content->Windows.end()) {
                    auto length=static_cast<DWORD>(std::min<long long>(256*1024,_content->Before.Length-offset));
                    Require(++_content->Requests<=512&&(_content->Bytes+=length)<=128LL*1024*1024,HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA));
                    std::vector<BYTE> window(length);Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));WinCheck(event.Value!=nullptr);OVERLAPPED io{};io.Offset=static_cast<DWORD>(offset);io.OffsetHigh=static_cast<DWORD>(static_cast<unsigned long long>(offset)>>32);io.hEvent=reinterpret_cast<HANDLE>(reinterpret_cast<ULONG_PTR>(event.Value)|1);
                    _content->ReadStage="local-stream-read";DWORD done{};if(!ReadFile(_content->Raw,window.data(),length,&done,&io)){auto error=GetLastError();Require(error==ERROR_IO_PENDING,HRESULT_FROM_WIN32(error));WinCheck(GetOverlappedResult(_content->Raw,&io,&done,TRUE));}Require(done==length,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
                    found=_content->Windows.emplace(offset,std::move(window)).first;
                }
                const auto begin=static_cast<size_t>(position-offset);auto length=std::min(take-copied,static_cast<DWORD>(found->second.size()-begin));memcpy(static_cast<BYTE*>(buffer)+copied,found->second.data()+begin,length);copied+=length;
            }
            _content->ReadStage="local-stream-after";_content->Verify();_content->ReadStage=nullptr;_position+=copied;if(read)*read=copied;return copied==count?S_OK:S_FALSE;
        }catch(const Failure& error){return error.Code;}catch(...){return E_FAIL;}
    }
    IFACEMETHODIMP Seek(LARGE_INTEGER move,DWORD origin,ULARGE_INTEGER* position)override{auto base=origin==STREAM_SEEK_SET?0:origin==STREAM_SEEK_CUR?_position:origin==STREAM_SEEK_END?_content->Before.Length:-1;if(base<0||move.QuadPart< -base||move.QuadPart>_content->Before.Length-base)return STG_E_INVALIDFUNCTION;_position=base+move.QuadPart;if(position)position->QuadPart=_position;return S_OK;}
    IFACEMETHODIMP Stat(STATSTG* result,DWORD)override{if(!result)return E_POINTER;*result={};result->type=STGTY_STREAM;result->cbSize.QuadPart=_content->Before.Length;result->grfMode=STGM_READ;return S_OK;}
    IFACEMETHODIMP Clone(IStream** result)override{if(!result)return E_POINTER;auto clone=Microsoft::WRL::Make<LocalStream>(_content,_position);return clone?clone.CopyTo(result):E_OUTOFMEMORY;}
    IFACEMETHODIMP Write(const void*,ULONG,ULONG*)override{return STG_E_ACCESSDENIED;}
    IFACEMETHODIMP SetSize(ULARGE_INTEGER)override{return STG_E_ACCESSDENIED;}
    IFACEMETHODIMP CopyTo(IStream*,ULARGE_INTEGER,ULARGE_INTEGER*,ULARGE_INTEGER*)override{return E_NOTIMPL;}
    IFACEMETHODIMP Commit(DWORD)override{return S_OK;}
    IFACEMETHODIMP Revert()override{return E_NOTIMPL;}
    IFACEMETHODIMP LockRegion(ULARGE_INTEGER,ULARGE_INTEGER,DWORD)override{return E_NOTIMPL;}
    IFACEMETHODIMP UnlockRegion(ULARGE_INTEGER,ULARGE_INTEGER,DWORD)override{return E_NOTIMPL;}
};
bool FullLocal(const std::wstring& path){auto file=OpenAttributes(path);auto stamp=InspectFile(file.Value);if(stamp.Directory||stamp.Links!=1)return false;FILE_ATTRIBUTE_TAG_INFO tag{};WinCheck(GetFileInformationByHandleEx(file.Value,FileAttributeTagInfo,&tag,sizeof(tag)));return Persisted(file.Value)?HasFullContent(file.Value,stamp.Length):(tag.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)==0;}
/// <summary>仅本次创建的子进程进入512MiB Job；仅继承输出句柄，两秒期限包括启动、读取和解码。</summary>
std::vector<BYTE> DecodeLocal(const std::wstring& path,UINT size) {
    const auto deadline=GetTickCount64()+2000;auto before=InspectFile(path);auto grant=ReadThumbnailGrant(path);
    Handle output,write;SECURITY_ATTRIBUTES security{sizeof(security),nullptr,TRUE};WinCheck(CreatePipe(&output.Value,&write.Value,&security,0));WinCheck(SetHandleInformation(output.Value,HANDLE_FLAG_INHERIT,0));
    SIZE_T length{};InitializeProcThreadAttributeList(nullptr,1,0,&length);std::vector<BYTE> memory(length);auto attributes=reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(memory.data());WinCheck(InitializeProcThreadAttributeList(attributes,1,0,&length));
    struct Attributes{LPPROC_THREAD_ATTRIBUTE_LIST Value;~Attributes(){DeleteProcThreadAttributeList(Value);}} owner{attributes};
    Handle acknowledged(CreateEventW(&security,TRUE,FALSE,nullptr));WinCheck(acknowledged.Value!=nullptr);
    HANDLE inherited[]{write.Value,acknowledged.Value};WinCheck(UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,inherited,sizeof(inherited),nullptr,nullptr));
    STARTUPINFOEXW startup{};startup.StartupInfo.cb=sizeof(startup);startup.StartupInfo.dwFlags=STARTF_USESTDHANDLES|STARTF_FORCEOFFFEEDBACK;startup.StartupInfo.hStdOutput=write.Value;startup.lpAttributeList=attributes;
    auto executable=ModuleDirectory()+L"\\HybridMount.Stock.exe";auto arguments=L"\""+executable+L"\" --local-thumbnail-worker \""+path+L"\" "+std::to_wstring(size)+L" "+std::to_wstring(reinterpret_cast<ULONG_PTR>(acknowledged.Value));PROCESS_INFORMATION process{};
    Handle job(CreateJobObjectW(nullptr,nullptr));WinCheck(job.Value!=nullptr);JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE|JOB_OBJECT_LIMIT_PROCESS_MEMORY;limits.ProcessMemoryLimit=512ULL*1024*1024;WinCheck(SetInformationJobObject(job.Value,JobObjectExtendedLimitInformation,&limits,sizeof(limits)));
    WinCheck(CreateProcessW(executable.c_str(),arguments.data(),nullptr,nullptr,TRUE,CREATE_NO_WINDOW|CREATE_SUSPENDED|EXTENDED_STARTUPINFO_PRESENT,nullptr,ModuleDirectory().c_str(),&startup.StartupInfo,&process));Handle child(process.hProcess),thread(process.hThread);
    if(!AssignProcessToJobObject(job.Value,child.Value)){TerminateProcess(child.Value,1);throw Failure(HRESULT_FROM_WIN32(GetLastError()));}
    WinCheck(ResumeThread(thread.Value)!=static_cast<DWORD>(-1));CloseHandle(write.Value);write.Value=nullptr;
    std::vector<BYTE> bytes;DWORD expected{};bool header=false;
    while(GetTickCount64()<deadline) {
        DWORD available{};if(!PeekNamedPipe(output.Value,nullptr,0,nullptr,&available,nullptr)){Require(GetLastError()==ERROR_BROKEN_PIPE,HRESULT_FROM_WIN32(GetLastError()));break;}
        if(available) {
            if(!header&&available>=4){Transfer(output.Value,&expected,4,false);Require(expected>0&&expected<=1024*1024);header=true;bytes.reserve(expected);available-=4;}
            if(header&&available){auto count=std::min(available,expected-static_cast<DWORD>(bytes.size()));auto old=bytes.size();bytes.resize(old+count);Transfer(output.Value,bytes.data()+old,count,false);}
            if(header&&bytes.size()==expected)break;
        }
        WaitForSingleObject(child.Value,10);
    }
    if(!header||bytes.size()!=expected){TerminateJobObject(job.Value,1);throw Failure(HRESULT_FROM_WIN32(ERROR_TIMEOUT));}
    auto result=Parse(std::string(bytes.begin(),bytes.end()),1024*1024);if(result.at("kind")!="localResult"){ThumbnailEvidence=result.value("evidence",Json::object());LocalFailureStage=result.value("stage","local-worker");if(LocalFailureStage.size()>64||!LocalFailureStage.starts_with("local-"))LocalFailureStage="local-worker";throw Failure(result.value("hresult",E_FAIL),LocalFailureStage.c_str());}
    if(result.at("initial")!=ContentStamp(before)||result.at("stamp")!=ContentStamp(InspectFile(path))||ReadThumbnailGrant(path)!=grant)throw Failure(HRESULT_FROM_WIN32(ERROR_FILE_INVALID),"local-parent-validation");
    auto png=Base64(result.at("png").get<std::string>());WinCheck(SetEvent(acknowledged.Value));return png;
}
}
std::vector<BYTE> ReadAvailableThumbnail(const std::wstring& path,UINT size) {
    Require(size>=1&&size<=256);try{auto cached=ReadCachedThumbnail(path,size);if(!cached.empty())return cached;}catch(const Failure&){}catch(const Json::exception&){}
    ThumbnailReadStage="local-authorization";ReadThumbnailGrant(path);ThumbnailReadStage="local-completeness";if(!FullLocal(path))return {};
    ThumbnailReadStage="local-decode";auto png=DecodeLocal(path,size);ThumbnailReadStage="local-ready";return png;
}
int RunLocalThumbnailWorker(const std::wstring& path,UINT size,HANDLE acknowledged)noexcept {
    auto initialized=CoInitializeEx(nullptr,COINIT_MULTITHREADED);struct Apartment{HRESULT Code;~Apartment(){if(SUCCEEDED(Code))CoUninitialize();}} apartment{initialized};
    std::shared_ptr<Content> content;
    try {
        Check(initialized);Require(size>=1&&size<=256);auto grant=ReadThumbnailGrant(path);content=std::make_shared<Content>(path,grant);auto stream=Microsoft::WRL::Make<LocalStream>(content);Require(stream!=nullptr);
        auto extension=path.substr(path.find_last_of(L'.'));for(auto& letter:extension)letter=static_cast<wchar_t>(towlower(letter));
        bool supported=false;for(auto item:{L".jpg",L".jpeg",L".png",L".bmp",L".gif",L".tif",L".tiff",L".heic",L".heif",L".webp",L".avif",L".mp4"})supported|=extension==item;Require(supported,E_NOTIMPL);
        auto module=LoadLibraryExW((ModuleDirectory()+L"\\HybridMount.Ext.dll").c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);Require(module!=nullptr,HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND));
        struct Module{HMODULE Value;~Module(){FreeLibrary(Value);}} owner{module};using Decode=HRESULT(WINAPI*)(IStream*,const wchar_t*,UINT,BYTE**,DWORD*);Decode decode{};auto proc=GetProcAddress(module,"HybridMountDecodePreview");Require(proc!=nullptr);memcpy(&decode,&proc,sizeof(decode));
        BYTE* png{};DWORD count{};auto status=decode(stream.Get(),extension.c_str(),std::clamp(size,16u,256u),&png,&count);struct Memory{BYTE* Value;~Memory(){CoTaskMemFree(Value);}} bytes{png};Check(status);Require(png&&count>=33&&count<=384*1024);
        auto final=content->Verify();if(ReadThumbnailGrant(path)!=grant)throw Failure(HRESULT_FROM_WIN32(ERROR_FILE_INVALID),"local-worker-validation");
        WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),{{"kind","localResult"},{"png",Base64(png,count)},{"initial",ContentStamp(content->Before)},{"stamp",ContentStamp(final)}});WaitForSingleObject(acknowledged,2500);return 0;
    }catch(const Failure& error){try{WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),{{"kind","unavailable"},{"hresult",error.Code},{"stage",content&&content->ReadStage?content->ReadStage:error.Stage},{"evidence",content?content->Evidence:Json::object()}});}catch(...){}return 1;}
    catch(...){try{WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),{{"kind","unavailable"},{"hresult",E_FAIL}});}catch(...){}return 1;}
}
}

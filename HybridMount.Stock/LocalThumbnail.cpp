#include "LocalThumbnail.h"
#include "LocalReadIo.h"
#include "LocalThumbnailQueue.h"
#include "NativeFile.h"
#include "NativeWire.h"
#include "NativeDiagnostics.h"
#include "PreviewWorker.h"
#include "RemoteThumbnailHints.h"
#include <thread>
#include <condition_variable>
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
    std::shared_ptr<LocalReadFile> File;std::atomic<bool> Closed{};ULONGLONG Deadline=GetTickCount64()+10000;FileState Before;std::mutex Gate;int Requests{};long long Bytes{};std::map<long long,std::shared_ptr<const std::vector<BYTE>>> Windows;std::atomic<const char*> ReadStage{};Json Evidence=Json::object();std::optional<Placeholder> Cloud;
    explicit Content(const std::wstring& path,const Json& grant) {
        ValidatePath(grant,path);auto probe=OpenAttributes(path);auto stamp=InspectFile(probe.Value);Require(!stamp.Directory&&stamp.Links==1,E_ACCESSDENIED);
        FILE_ATTRIBUTE_TAG_INFO tag{};WinCheck(GetFileInformationByHandleEx(probe.Value,FileAttributeTagInfo,&tag,sizeof(tag)));
        const auto persisted=Persisted(probe.Value);const auto cloud=persisted.has_value();
        if(cloud) {
            auto info=InspectPlaceholder(probe.Value);auto marker=Parse(std::string(info.Identity.begin(),info.Identity.end()),4096);Require(GuidN(marker.at("MountId"))==GuidN(grant.at("MountId")),E_ACCESSDENIED);
            Require(HasFullContent(probe.Value,stamp.Length),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));
        } else Require((tag.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)==0,E_ACCESSDENIED);
        File=std::make_shared<LocalReadFile>(path,cloud);
        try{Before=InspectFile(File->Raw);if(ContentStamp(Before)!=ContentStamp(stamp))throw Failure(HRESULT_FROM_WIN32(ERROR_FILE_INVALID),"local-open-stamp");if(cloud){Require(HasFullContent(File->Raw,Before.Length),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));Cloud=InspectPlaceholder(File->Raw);}}
        catch(...){Close();throw;}
    }
    /// <summary>保护引用阻止外部正文变更；读取可推进访问元数据USN，只核验受保护正文及真实CFAPI状态。</summary>
    FileState Verify() {
        std::shared_ptr<LocalReadFile> file;{std::lock_guard lock(Gate);Require(!Closed&&File!=nullptr,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));file=File;}
        Require(!file->Broken(),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));auto current=InspectFile(file->Raw);Require(current.Identity==Before.Identity&&current.Length==Before.Length&&current.LastWrite==Before.LastWrite&&!current.Directory&&current.Links==Before.Links,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        if(Cloud){auto info=InspectPlaceholder(file->Raw);Require(info.Identity==Cloud->Identity&&info.InSync==Cloud->InSync&&info.Pin==Cloud->Pin&&info.Modified==Cloud->Modified&&info.OnDisk==Cloud->OnDisk&&HasFullContent(file->Raw,current.Length),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));}Require(!Closed,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));return current;
    }
    /// <summary>缓存查询／预算只持短锁；I/O、oplock及最终戳核验均在锁外。迟到读取不能恢复已关闭窗口。</summary>
    std::shared_ptr<const std::vector<BYTE>> Window(long long offset){
        std::shared_ptr<LocalReadFile> file;const auto length=static_cast<DWORD>(std::min<long long>(256*1024,Before.Length-offset));
        {std::lock_guard lock(Gate);Require(!Closed,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));auto found=Windows.find(offset);if(found!=Windows.end())return found->second;Require(++Requests<=512&&(Bytes+=length)<=128LL*1024*1024,HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA));file=File;}
        ReadStage="local-stream-before";Verify();ReadStage="local-stream-read";auto bytes=std::make_shared<const std::vector<BYTE>>(ReadLocalFile(file,offset,length,nullptr,nullptr,Deadline));ReadStage="local-stream-after";Verify();
        {std::lock_guard lock(Gate);Require(!Closed,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));auto found=Windows.emplace(offset,bytes);ReadStage=nullptr;return found.first->second;}
    }
    /// <summary>关闭不等待内容锁内的I/O；未完成操作保留独立所有者直到内核完成，编解码器迟到读取返回取消。</summary>
    void Close(){std::shared_ptr<LocalReadFile> file;{std::lock_guard lock(Gate);if(Closed.exchange(true))return;file=std::move(File);Windows.clear();}if(file){file->RequestClose();file->Drain(250);}}

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
            Require(!_content->Closed,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));auto take=static_cast<DWORD>(std::min<long long>(count,_content->Before.Length-_position));if(!take)return count?S_FALSE:S_OK;
            _content->Verify();DWORD copied{};
            while(copied<take){auto position=_position+copied;auto offset=position/(256*1024)*(256*1024);auto window=_content->Window(offset);const auto begin=static_cast<size_t>(position-offset);auto length=std::min(take-copied,static_cast<DWORD>(window->size()-begin));memcpy(static_cast<BYTE*>(buffer)+copied,window->data()+begin,length);copied+=length;}
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
bool FullLocal(const std::wstring& path){auto dot=path.find_last_of(L'.');if(dot==std::wstring::npos)return false;auto extension=path.substr(dot);for(auto& letter:extension)letter=static_cast<wchar_t>(towlower(letter));if(!SupportsPreviewExtension(extension))return false;auto file=OpenAttributes(path);auto stamp=InspectFile(file.Value);if(stamp.Directory||stamp.Links!=1||stamp.Length==0)return false;FILE_ATTRIBUTE_TAG_INFO tag{};WinCheck(GetFileInformationByHandleEx(file.Value,FileAttributeTagInfo,&tag,sizeof(tag)));return Persisted(file.Value)?HasFullContent(file.Value,stamp.Length):(tag.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)==0;}
/// <summary>常驻单并发解码端，只有匿名输入／输出可继承；启动5秒与单项解码10秒分开计时，闲置30秒退出。</summary>
class LocalDecoder;
std::atomic<LocalDecoder*> CurrentDecoder{};
class LocalDecoder {
    std::timed_mutex Gate;std::condition_variable_any Changed;Handle Process{nullptr},Input{nullptr},Output{nullptr},Job{nullptr};std::thread Idle;
    bool Closed{};ULONGLONG LastUse{},Starts{},Decodes{},IdleExits{};DWORD ChildId{};
    static void Reset(Handle& handle){if(handle.Value&&handle.Value!=INVALID_HANDLE_VALUE)CloseHandle(handle.Value);handle.Value=nullptr;}
    void CloseChild(bool graceful=false)noexcept{
        if(Process.Value&&Process.Value!=INVALID_HANDLE_VALUE){
            if(graceful&&WaitForSingleObject(Process.Value,0)==WAIT_TIMEOUT)try{WriteFrame(Input.Value,{{"kind","stop"}});WaitForSingleObject(Process.Value,500);}catch(...){}
            if(WaitForSingleObject(Process.Value,0)==WAIT_TIMEOUT&&Job.Value)TerminateJobObject(Job.Value,1);WaitForSingleObject(Process.Value,3000);
        }
        Reset(Input);Reset(Output);Reset(Process);Reset(Job);ChildId=0;
    }
    Json Receive(ULONGLONG deadline,HANDLE stopping){
        std::vector<BYTE> bytes;DWORD expected{};bool header=false;
        while(GetTickCount64()<deadline&&(!stopping||WaitForSingleObject(stopping,0)==WAIT_TIMEOUT)){
            DWORD available{};if(!PeekNamedPipe(Output.Value,nullptr,0,nullptr,&available,nullptr))throw Failure(HRESULT_FROM_WIN32(GetLastError()),"local-worker-channel");
            if(available){
                if(!header&&available>=4){Transfer(Output.Value,&expected,4,false);Require(expected>0&&expected<=1024*1024);header=true;bytes.reserve(expected);available-=4;}
                if(header&&available){auto count=std::min(available,expected-static_cast<DWORD>(bytes.size()));auto old=bytes.size();bytes.resize(old+count);Transfer(Output.Value,bytes.data()+old,count,false);}
                if(header&&bytes.size()==expected)return Parse(std::string(bytes.begin(),bytes.end()),1024*1024);
            }
            if(WaitForSingleObject(Process.Value,0)==WAIT_OBJECT_0)throw Failure(HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE),"local-worker-exit");
            Sleep(5);
        }
        throw Failure(HRESULT_FROM_WIN32(stopping&&WaitForSingleObject(stopping,0)!=WAIT_TIMEOUT?ERROR_OPERATION_ABORTED:ERROR_TIMEOUT),"local-worker-deadline");
    }
    void Start(HANDLE stopping,LocalThumbnailTiming* timing){
        CloseChild();SECURITY_ATTRIBUTES security{sizeof(security),nullptr,TRUE};Handle inputRead,outputWrite;
        WinCheck(CreatePipe(&inputRead.Value,&Input.Value,&security,65536));WinCheck(SetHandleInformation(Input.Value,HANDLE_FLAG_INHERIT,0));
        WinCheck(CreatePipe(&Output.Value,&outputWrite.Value,&security,65536));WinCheck(SetHandleInformation(Output.Value,HANDLE_FLAG_INHERIT,0));
        SIZE_T length{};InitializeProcThreadAttributeList(nullptr,1,0,&length);std::vector<BYTE> memory(length);auto attributes=reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(memory.data());WinCheck(InitializeProcThreadAttributeList(attributes,1,0,&length));
        struct Attributes{LPPROC_THREAD_ATTRIBUTE_LIST Value;~Attributes(){DeleteProcThreadAttributeList(Value);}} owner{attributes};
        HANDLE inherited[]{inputRead.Value,outputWrite.Value};WinCheck(UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,inherited,sizeof(inherited),nullptr,nullptr));
        STARTUPINFOEXW startup{};startup.StartupInfo.cb=sizeof(startup);startup.StartupInfo.dwFlags=STARTF_USESTDHANDLES|STARTF_FORCEOFFFEEDBACK;startup.StartupInfo.hStdInput=inputRead.Value;startup.StartupInfo.hStdOutput=outputWrite.Value;startup.lpAttributeList=attributes;
        Job.Value=CreateJobObjectW(nullptr,nullptr);WinCheck(Job.Value!=nullptr);JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE|JOB_OBJECT_LIMIT_PROCESS_MEMORY;limits.ProcessMemoryLimit=512ULL*1024*1024;WinCheck(SetInformationJobObject(Job.Value,JobObjectExtendedLimitInformation,&limits,sizeof(limits)));
        auto executable=ModuleDirectory()+L"\\HybridMount.Stock.exe";auto arguments=L"\""+executable+L"\" --local-thumbnail-service";PROCESS_INFORMATION info{};const auto started=GetTickCount64();
        WinCheck(CreateProcessW(executable.c_str(),arguments.data(),nullptr,nullptr,TRUE,CREATE_NO_WINDOW|CREATE_SUSPENDED|EXTENDED_STARTUPINFO_PRESENT,nullptr,ModuleDirectory().c_str(),&startup.StartupInfo,&info));Process.Value=info.hProcess;Handle thread(info.hThread);ChildId=info.dwProcessId;
        if(!AssignProcessToJobObject(Job.Value,Process.Value)){auto error=GetLastError();TerminateProcess(Process.Value,1);throw Failure(HRESULT_FROM_WIN32(error));}
        WinCheck(ResumeThread(thread.Value)!=static_cast<DWORD>(-1));Reset(inputRead);Reset(outputWrite);
        Require(Receive(started+5000,stopping).at("kind")=="localReady");++Starts;if(timing){timing->Started=true;timing->Startup=GetTickCount64()-started;}
    }
    void Reap(){
        std::unique_lock lock(Gate);
        while(!Closed){
            if(!Process.Value){Changed.wait(lock,[&]{return Closed||Process.Value;});continue;}
            const auto now=GetTickCount64();if(now-LastUse>=30000){CloseChild(true);++IdleExits;continue;}
            Changed.wait_for(lock,std::chrono::milliseconds(30000-(now-LastUse)));
        }
    }
public:
    LocalDecoder(){CurrentDecoder.store(this);Idle=std::thread([this]{Reap();});}
    ~LocalDecoder(){Stop();CurrentDecoder.store(nullptr);}
    Json Read(const std::wstring& path,UINT size,HANDLE stopping,LocalThumbnailTiming* timing,ULONGLONG budget=10000){
        std::unique_lock<std::timed_mutex> lock(Gate,std::defer_lock);const auto waiting=GetTickCount64();
        while(!lock.try_lock_for(std::chrono::milliseconds(10))){if(GetTickCount64()-waiting>=budget||(stopping&&WaitForSingleObject(stopping,0)!=WAIT_TIMEOUT))throw Failure(HRESULT_FROM_WIN32(ERROR_TIMEOUT),"local-worker-wait");}
        Require(!Closed,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));
        try{
            if(!Process.Value||WaitForSingleObject(Process.Value,0)==WAIT_OBJECT_0)Start(stopping,timing);
            auto request=Json{{"kind","decode"},{"path",Utf8(path)},{"size",size}};Require(request.dump().size()<=32768,E_NOTIMPL);WriteFrame(Input.Value,request);
            auto response=Receive(GetTickCount64()+budget,stopping);LastUse=GetTickCount64();Changed.notify_all();++Decodes;return response;
        }catch(...){CloseChild();Changed.notify_all();throw;}
    }
    Json Statistics(){std::unique_lock lock(Gate,std::try_to_lock);if(!lock.owns_lock())return {{"busy",true}};return {{"started",Starts},{"decoded",Decodes},{"idleExits",IdleExits},{"pid",ChildId}};}
    void Stop()noexcept{try{{std::lock_guard lock(Gate);if(!Closed){Closed=true;CloseChild(true);Changed.notify_all();}}if(Idle.joinable())Idle.join();}catch(...) {}}
};
LocalDecoder& Decoder(){static LocalDecoder value;return value;}
/// <summary>调用前后重验授权及内容戳；常驻进程仅复用解码器，每项重新取得并释放该文件保护引用。</summary>
std::vector<BYTE> DecodeLocal(const std::wstring& path,UINT size,HANDLE stopping=nullptr,std::string* verifiedStamp=nullptr,LocalThumbnailTiming* timing=nullptr,Json* frames=nullptr){
    auto before=InspectFile(path);auto grant=ReadBackgroundThumbnailGrant(path,stopping);auto result=Decoder().Read(path,size,stopping,timing);
    if(timing)timing->Decode=result.value("decodeMs",0ULL);
    if(result.at("kind")!="localResult"){ThumbnailEvidence=result.value("evidence",Json::object());LocalFailureStage=result.value("stage","local-worker");if(LocalFailureStage.size()>64||!LocalFailureStage.starts_with("local-"))LocalFailureStage="local-worker";throw Failure(result.value("hresult",E_FAIL),LocalFailureStage.c_str());}
    if(result.at("initial")!=ContentStamp(before))throw Failure(HRESULT_FROM_WIN32(ERROR_FILE_INVALID),"local-parent-initial");
    auto png=Base64(result.at("png").get<std::string>());const auto stamp=result.at("stamp").get<std::string>();
    if(stamp!=ContentStamp(InspectFile(path))||ReadBackgroundThumbnailGrant(path,stopping)!=grant)throw Failure(HRESULT_FROM_WIN32(ERROR_FILE_INVALID),"local-parent-validation");
    if(verifiedStamp)*verifiedStamp=stamp;if(frames)*frames=result.at("frames");return png;
}
}
Json ReadBackgroundThumbnailGrant(const std::wstring& path,HANDLE stopping){
    const auto deadline=GetTickCount64()+100;
    while(true)try{return ReadThumbnailGrant(path,true);}catch(const Failure& error){
        if(error.Code!=HRESULT_FROM_WIN32(ERROR_BUSY)||GetTickCount64()>=deadline)throw;
        if(stopping){if(WaitForSingleObject(stopping,2)!=WAIT_TIMEOUT)throw Failure(HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));}else Sleep(2);
    }
}
std::string LocalThumbnailStamp(const std::wstring& path){return ContentStamp(InspectFile(path));}
std::vector<BYTE> DecodeLocalThumbnail(const std::wstring& path,HANDLE stopping,std::string& verifiedStamp,LocalThumbnailTiming& timing,Json* frames){return DecodeLocal(path,256,stopping,&verifiedStamp,&timing,frames);}
void StopLocalThumbnailDecoder()noexcept{if(auto decoder=CurrentDecoder.load())decoder->Stop();}
Json LocalThumbnailDecoderStatistics(){if(auto decoder=CurrentDecoder.load())return decoder->Statistics();return {{"started",0},{"decoded",0},{"idleExits",0},{"pid",0}};}
std::vector<BYTE> ReadAvailableThumbnail(const std::wstring& path,UINT size,bool asynchronous) {
    Require(size>=1&&size<=256);Json grant;auto cached=ReadThumbnailSnapshot(path,size,grant);if(!cached.empty())return cached;
    ThumbnailReadStage="local-completeness";if(!FullLocal(path)){
        auto dot=path.find_last_of(L'.');if(dot!=std::wstring::npos){auto extension=path.substr(dot);for(auto& letter:extension)letter=static_cast<wchar_t>(towlower(letter));if(asynchronous&&SupportsPreviewExtension(extension))QueueRemoteThumbnailHint(path,size,LocalThumbnailStamp(path),grant);}return {};
    }
    if(!asynchronous){ThumbnailReadStage="local-decode";auto png=DecodeLocal(path,size);ThumbnailReadStage="local-ready";return png;}
    const auto stamp=LocalThumbnailStamp(path);auto png=QueueLocalThumbnail(path,stamp,grant);
    if(!png.empty()){Require(LocalThumbnailStamp(path)==stamp,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));ThumbnailReadStage="local-ready";}else ThumbnailReadStage="local-queued";
    return png;
}
int RunLocalThumbnailWorker(const std::wstring& path,UINT size,HANDLE acknowledged)noexcept {
    auto initialized=CoInitializeEx(nullptr,COINIT_MULTITHREADED);struct Apartment{HRESULT Code;~Apartment(){if(SUCCEEDED(Code))CoUninitialize();}} apartment{initialized};
    std::shared_ptr<Content> content;ULONGLONG decodeMilliseconds{};
    try {
        Check(initialized);if(!SetPriorityClass(GetCurrentProcess(),PROCESS_MODE_BACKGROUND_BEGIN))Require(GetLastError()==ERROR_PROCESS_MODE_ALREADY_BACKGROUND);Require(size>=1&&size<=256);auto grant=ReadBackgroundThumbnailGrant(path);
        // 命名互斥量先于任何正文保护引用；与托管生成者按同SID及NTFS身份共享。
        auto identity=InspectFile(path).Identity;auto hash=Sha256(Utf8(CurrentSid())+"|"+identity);for(auto& letter:hash)letter=static_cast<char>(tolower(letter));
        Handle mutex(CreateMutexW(nullptr,FALSE,(L"Local\\HybridMount.Preview.Decode."+Wide(hash)).c_str()));WinCheck(mutex.Value!=nullptr);
        auto acquired=WaitForSingleObject(mutex.Value,1500);Require(acquired==WAIT_OBJECT_0||acquired==WAIT_ABANDONED,HRESULT_FROM_WIN32(ERROR_TIMEOUT));
        struct MutexOwner{HANDLE Value;~MutexOwner(){ReleaseMutex(Value);}} mutexOwner{mutex.Value};
#ifdef HYBRIDMOUNT_NATIVE_TESTS
        // 仅隔离映像可注入慢解码；产品映像不读取此变量；单项解码期限由父进程独立控制。
        wchar_t delay[16]{};if(GetEnvironmentVariableW(L"HYBRIDMOUNT_TEST_LOCAL_DELAY_MS",delay,16)){auto milliseconds=wcstoul(delay,nullptr,10);Sleep(std::min(milliseconds,15000ul));}
#endif
        content=std::make_shared<Content>(path,grant);auto stream=Microsoft::WRL::Make<LocalStream>(content);Require(stream!=nullptr);
        auto extension=path.substr(path.find_last_of(L'.'));for(auto& letter:extension)letter=static_cast<wchar_t>(towlower(letter));
        Require(SupportsPreviewExtension(extension),E_NOTIMPL);const auto decoding=GetTickCount64();auto frames=DecodePreviewFrames(stream.Get(),extension);decodeMilliseconds=GetTickCount64()-decoding;
        auto final=content->Verify();content->Close();auto closed=InspectFile(path);
        Require(closed.Identity==final.Identity&&closed.Length==final.Length&&closed.LastWrite==final.LastWrite,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        // 只吸收本次只读保护引用关闭造成的已知元数据USN；无法证明或包含正文变化时拒绝。
        if(ContentStamp(closed)!=ContentStamp(final))Require(final.Usn>0&&closed.Usn>final.Usn&&(closed.Reasons&(USN_REASON_DATA_OVERWRITE|USN_REASON_DATA_EXTEND|USN_REASON_DATA_TRUNCATION|USN_REASON_NAMED_DATA_OVERWRITE|USN_REASON_NAMED_DATA_EXTEND|USN_REASON_NAMED_DATA_TRUNCATION))==0,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        if(content->Cloud){auto file=OpenAttributes(path);auto info=InspectPlaceholder(file.Value);Require(info.Identity==content->Cloud->Identity&&info.InSync==content->Cloud->InSync&&info.Pin==content->Cloud->Pin&&info.Modified==content->Cloud->Modified&&HasFullContent(file.Value,closed.Length),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));}
        final=closed;if(ReadBackgroundThumbnailGrant(path)!=grant)throw Failure(HRESULT_FROM_WIN32(ERROR_FILE_INVALID),"local-worker-validation");
        WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),{{"kind","localResult"},{"png",frames.at("256")},{"frames",frames},{"decodeMs",decodeMilliseconds},{"initial",ContentStamp(content->Before)},{"stamp",ContentStamp(final)}});if(acknowledged)WaitForSingleObject(acknowledged,2500);return 0;
    }catch(const Failure& error){if(content)content->Close();try{WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),{{"kind","unavailable"},{"decodeMs",decodeMilliseconds},{"hresult",error.Code},{"stage",content&&content->ReadStage.load()?content->ReadStage.load():error.Stage},{"evidence",content?content->Evidence:Json::object()}});}catch(...){}return 1;}
    catch(...){if(content)content->Close();try{WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),{{"kind","unavailable"},{"hresult",E_FAIL}});}catch(...){}return 1;}
}
int RunLocalThumbnailService()noexcept{
    const auto initialized=CoInitializeEx(nullptr,COINIT_MULTITHREADED);struct Apartment{HRESULT Code;~Apartment(){if(SUCCEEDED(Code))CoUninitialize();}} apartment{initialized};
    try{Check(initialized);WinCheck(SetPriorityClass(GetCurrentProcess(),PROCESS_MODE_BACKGROUND_BEGIN));WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),{{"kind","localReady"}});
        while(true){auto request=ReadFrame(GetStdHandle(STD_INPUT_HANDLE));if(request.at("kind")=="stop")return 0;Require(request.at("kind")=="decode");const auto size=Integer(request,"size");Require(size>=1&&size<=256);RunLocalThumbnailWorker(FullPath(Text(request,"path")),static_cast<UINT>(size),nullptr);}}
    catch(...){return 1;}
}

}

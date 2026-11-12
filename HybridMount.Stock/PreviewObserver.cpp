#include "PreviewObserver.h"
#include "VisiblePreviewItems.h"
#include "NativeFile.h"
#include "RuntimeOperations.h"
#include "CloudFileOperations.h"
#include <set>
#include "NativeDiagnostics.h"
#include <exdisp.h>
#include <servprov.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <wrl.h>
#include <optional>
#include <future>
#include <thread>
#include <mutex>
#include <map>
#include <atomic>
#include <algorithm>
using Microsoft::WRL::ComPtr;
namespace hm {
namespace {
thread_local Json ViewDiagnostics=Json::object();
// WinEvent只由控制线程接收；普通窗口的滚动不会使其他窗口失效。
std::map<HWND,ULONGLONG> DirtyWindows;
std::set<HWND> ManagedWindows;
void CALLBACK ViewEvent(HWINEVENTHOOK,DWORD event,HWND window,LONG object,LONG,DWORD,DWORD){
    if(!window)return;const auto root=GetAncestor(window,GA_ROOT);wchar_t name[64]{};GetClassNameW(root,name,64);if(_wcsicmp(name,L"CabinetWClass"))return;
    const bool navigation=event==EVENT_OBJECT_NAMECHANGE||event==EVENT_OBJECT_VALUECHANGE||event==EVENT_OBJECT_PARENTCHANGE||
        ((event==EVENT_OBJECT_CREATE||event==EVENT_OBJECT_DESTROY)&&object==OBJID_WINDOW);
    if(navigation||ManagedWindows.contains(root)){
        if(DirtyWindows.size()<32||DirtyWindows.contains(root))DirtyWindows[root]=GetTickCount64();
    }
}
struct ViewEvents {
    HWINEVENTHOOK Objects=SetWinEventHook(EVENT_OBJECT_CREATE,EVENT_OBJECT_CONTENTSCROLLED,nullptr,ViewEvent,0,0,WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS);
    HWINEVENTHOOK Scroll=SetWinEventHook(EVENT_SYSTEM_SCROLLINGSTART,EVENT_SYSTEM_SCROLLINGEND,nullptr,ViewEvent,0,0,WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS);
    ~ViewEvents(){if(Objects)UnhookWinEvent(Objects);if(Scroll)UnhookWinEvent(Scroll);}
};
bool Media(const std::wstring& path){auto dot=path.find_last_of(L'.');if(dot==std::wstring::npos)return false;auto extension=path.substr(dot);for(auto& letter:extension)letter=static_cast<wchar_t>(towlower(letter));for(auto item:{L".jpg",L".jpeg",L".png",L".bmp",L".gif",L".tif",L".tiff",L".heic",L".heif",L".webp",L".avif",L".mp4",L".exe"})if(extension==item)return true;return false;}
/// <summary>优先真实文件系统路径；侧栏别名下的项目可退回已核验目录内的单一文件名，不接收任意相对路径。</summary>
std::optional<std::wstring> ItemPath(IShellItem* item,const std::wstring& directory=L""){PWSTR raw{};if(FAILED(item->GetDisplayName(SIGDN_FILESYSPATH,&raw))){if(directory.empty()||FAILED(item->GetDisplayName(SIGDN_PARENTRELATIVEPARSING,&raw)))return {};std::wstring name(raw);CoTaskMemFree(raw);if(name.empty()||name==L"."||name==L".."||name.find_first_of(L"\\/:")!=std::wstring::npos)return {};try{return FullPath(directory+L"\\"+name);}catch(...){return {};}}std::wstring path(raw);CoTaskMemFree(raw);try{return FullPath(path);}catch(...){return {};}}
/// <summary>唯一目录识别STA拥有Shell接口。控制线程只交付HWND和绝对期限，队列至多一项；超时后不再叠加RPC。</summary>
class DirectoryReader {
public:
    struct Request{HWND Window{};ULONGLONG Deadline{};std::atomic<bool> Expired{};std::promise<Json> Result;};
private:
    Json Roots;Handle Wake{CreateEventW(nullptr,FALSE,FALSE,nullptr)};std::mutex Gate;
    std::shared_ptr<Request> Pending;bool Closed{};std::thread Worker;
    std::map<HWND,ComPtr<IWebBrowserApp>> Browsers;ULONGLONG Mapped{};
    static void Deadline(Request& request){Require(!request.Expired&&GetTickCount64()<request.Deadline,HRESULT_FROM_WIN32(ERROR_TIMEOUT));}
    ComPtr<IWebBrowserApp> Browser(Request& request){
        const auto now=GetTickCount64();
        if(Browsers.contains(request.Window)&&now-Mapped<5000)return Browsers.at(request.Window);
        Deadline(request);ComPtr<IShellWindows> windows;Check(CoCreateInstance(CLSID_ShellWindows,nullptr,CLSCTX_LOCAL_SERVER,IID_PPV_ARGS(&windows)));
        long count{};Check(windows->get_Count(&count));Deadline(request);std::map<HWND,ComPtr<IWebBrowserApp>> found;
        for(long i=0;i<count&&i<32;++i){
            Deadline(request);VARIANT index{};index.vt=VT_I4;index.lVal=i;ComPtr<IDispatch> dispatch;Check(windows->Item(index,&dispatch));if(!dispatch)continue;
            ComPtr<IWebBrowserApp> browser;if(FAILED(dispatch.As(&browser)))continue;SHANDLE_PTR raw{};if(FAILED(browser->get_HWND(&raw)))continue;
            auto window=reinterpret_cast<HWND>(raw);if(IsWindow(window))found[window]=std::move(browser);
        }
        Deadline(request);Browsers=std::move(found);Mapped=GetTickCount64();
        auto value=Browsers.find(request.Window);return value==Browsers.end()?ComPtr<IWebBrowserApp>{}:value->second;
    }
    const Json* Managed(const std::wstring& directory)const{
        for(const auto& root:Roots){const auto base=FullPath(Text(root,"rootPath"));
            if(SamePath(base,directory)||(directory.size()>base.size()&&directory[base.size()]==L'\\'&&SamePath(base,directory.substr(0,base.size()))))return &root;
        }return nullptr;
    }
    Json Inspect(Request& request){
        const auto started=GetTickCount64();const char* stage="window";Json result={{"owned",false},{"complete",false},{"paths",Json::array()}};
        try{
            Deadline(request);Require(IsWindow(request.Window)&&IsWindowVisible(request.Window)&&!IsIconic(request.Window),HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE));
            DWORD pid{},session{},current{};GetWindowThreadProcessId(request.Window,&pid);Require(ProcessIdToSessionId(pid,&session)&&ProcessIdToSessionId(GetCurrentProcessId(),&current)&&session==current,E_ACCESSDENIED);
            Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid));WinCheck(process.Value!=nullptr);wchar_t image[32768]{};DWORD size=32768;WinCheck(QueryFullProcessImageNameW(process.Value,0,image,&size));
            const std::wstring imagePath(image,size);Require(_wcsicmp(imagePath.substr(imagePath.find_last_of(L'\\')+1).c_str(),L"explorer.exe")==0,E_ACCESSDENIED);
            stage="mapping";auto browser=Browser(request);Deadline(request);Require(browser!=nullptr,HRESULT_FROM_WIN32(ERROR_NOT_FOUND));
            stage="location";std::optional<std::wstring> directory;BSTR raw{};const auto location=browser->get_LocationURL(&raw);std::wstring url;
            if(SUCCEEDED(location)&&raw)url.assign(raw,SysStringLen(raw));if(raw)SysFreeString(raw);Deadline(request);
            if(url.starts_with(L"file:///")){std::wstring path(32768,L'\0');DWORD length=static_cast<DWORD>(path.size());if(SUCCEEDED(PathCreateFromUrlW(url.c_str(),path.data(),&length,0))){path.resize(wcslen(path.c_str()));directory=FullPath(path);}}
            // 普通文件系统目录在捕获Shell视图／UIA之前排除，不查根标记或文件属性。
            if(directory&&!Managed(*directory)){result["complete"]=true;result["filtered"]=true;result["directoryMs"]=GetTickCount64()-started;result["totalMs"]=GetTickCount64()-started;return result;}
            stage="view";ComPtr<IServiceProvider> services;Check(browser.As(&services));ComPtr<IShellBrowser> shell;Check(services->QueryService(SID_STopLevelBrowser,IID_PPV_ARGS(&shell)));Deadline(request);
            ComPtr<IShellView> view;Check(shell->QueryActiveShellView(&view));Deadline(request);ComPtr<IFolderView2> folderView;Check(view.As(&folderView));
            if(!directory){
                stage="directory";ComPtr<IShellFolder> folder;Check(folderView->GetFolder(IID_PPV_ARGS(&folder)));Deadline(request);ComPtr<IPersistFolder2> persist;Check(folder.As(&persist));PIDLIST_ABSOLUTE pidl{};Check(persist->GetCurFolder(&pidl));
                struct Pidl{PIDLIST_ABSOLUTE Value;~Pidl(){CoTaskMemFree(Value);}} owner{pidl};ComPtr<IShellItem> item;Check(SHCreateItemFromIDList(pidl,IID_PPV_ARGS(&item)));directory=ItemPath(item.Get());Deadline(request);
                if(!directory){ComPtr<IPersistFolder3> target;PERSIST_FOLDER_TARGET_INFO info{};
                    if(SUCCEEDED(folder.As(&target))&&SUCCEEDED(target->GetFolderTargetInfo(&info))){Pidl targetOwner{info.pidlTargetFolder};if(info.pidlTargetFolder){ComPtr<IShellItem> targetItem;if(SUCCEEDED(SHCreateItemFromIDList(info.pidlTargetFolder,IID_PPV_ARGS(&targetItem))))directory=ItemPath(targetItem.Get());}if(!directory&&info.szTargetParsingName[0])directory=FullPath(info.szTargetParsingName);}
                }
            }
            Deadline(request);if(!directory||!Managed(*directory)){result["complete"]=true;result["filtered"]=true;result["directoryMs"]=GetTickCount64()-started;result["totalMs"]=GetTickCount64()-started;return result;}
            stage="root";const auto marker=RootMarker(*directory);Deadline(request);Require(GuidN(marker.at("MountId"))==GuidN(Managed(*directory)->at("mountId")),E_ACCESSDENIED);
            result["owned"]=true;result["directory"]=Utf8(*directory);result["directoryMs"]=GetTickCount64()-started;
            stage="viewport";const auto sample=ReadVisiblePreviewItems(view.Get(),folderView.Get(),*directory,request.Deadline);Deadline(request);
            result["complete"]=sample.Complete;result["paths"]=sample.Paths;result["candidates"]=sample.Candidates;result["sampleMs"]=sample.Milliseconds;result["hresult"]=sample.Error;
        }catch(const Failure& error){result["hresult"]=error.Code;result["stage"]=stage;}catch(...){result["hresult"]=E_FAIL;result["stage"]=stage;}
        result["totalMs"]=GetTickCount64()-started;return result;
    }
    void Run()noexcept{
        const auto initialized=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        while(true){
            MsgWaitForMultipleObjects(1,&Wake.Value,FALSE,100,QS_ALLINPUT);MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
            std::shared_ptr<Request> request;{std::lock_guard lock(Gate);if(Closed)break;request=std::move(Pending);}if(!request)continue;
            Json result=SUCCEEDED(initialized)?Inspect(*request):Json{{"owned",false},{"complete",false},{"hresult",initialized}};
            result["finishedTick"]=GetTickCount64();try{request->Result.set_value(std::move(result));}catch(...){}
        }
        Browsers.clear();if(SUCCEEDED(initialized))CoUninitialize();
    }
public:
    explicit DirectoryReader(Json roots):Roots(std::move(roots)){WinCheck(Wake.Value!=nullptr);Worker=std::thread([this]{Run();});}
    ~DirectoryReader(){{std::lock_guard lock(Gate);Closed=true;}SetEvent(Wake.Value);if(Worker.joinable())Worker.join();}
    std::shared_ptr<Request> Submit(HWND window){auto request=std::make_shared<Request>();request->Window=window;request->Deadline=GetTickCount64()+250;{std::lock_guard lock(Gate);Require(!Pending);Pending=request;}SetEvent(Wake.Value);return request;}
};
/// <summary>Windows命令行转义；路径仅来自当前已核验根，不执行脚本或拼接Shell命令。</summary>
std::wstring Argument(const std::wstring& value){std::wstring result=L"\"";size_t slashes{};for(auto ch:value){if(ch==L'\\'){++slashes;continue;}if(ch==L'"'){result.append(slashes*2+1,L'\\');result+=ch;}else{result.append(slashes,L'\\');result+=ch;}slashes=0;}result.append(slashes*2,L'\\');return result+L"\"";}
struct Child {
    Handle Job,Process,Output;
    explicit Child(const std::map<std::string,Json>& roots){
        Handle write;SECURITY_ATTRIBUTES security{sizeof(security),nullptr,TRUE};WinCheck(CreatePipe(&Output.Value,&write.Value,&security,0));WinCheck(SetHandleInformation(Output.Value,HANDLE_FLAG_INHERIT,0));
        SIZE_T size{};InitializeProcThreadAttributeList(nullptr,1,0,&size);std::vector<BYTE> memory(size);auto attributes=reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(memory.data());WinCheck(InitializeProcThreadAttributeList(attributes,1,0,&size));struct Attributes{LPPROC_THREAD_ATTRIBUTE_LIST Value;~Attributes(){DeleteProcThreadAttributeList(Value);}} owner{attributes};
        HANDLE inherited[]{write.Value};WinCheck(UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,inherited,sizeof(inherited),nullptr,nullptr));STARTUPINFOEXW startup{};startup.StartupInfo.cb=sizeof(startup);startup.StartupInfo.dwFlags=STARTF_USESTDHANDLES|STARTF_FORCEOFFFEEDBACK;startup.StartupInfo.hStdOutput=write.Value;startup.lpAttributeList=attributes;
        Job.Value=CreateJobObjectW(nullptr,nullptr);WinCheck(Job.Value!=nullptr);JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE|JOB_OBJECT_LIMIT_PROCESS_MEMORY;limits.ProcessMemoryLimit=128ULL*1024*1024;WinCheck(SetInformationJobObject(Job.Value,JobObjectExtendedLimitInformation,&limits,sizeof(limits)));
        auto executable=ModuleDirectory()+L"\\HybridMount.Stock.exe",arguments=L"\""+executable+L"\" --preview-observer";for(auto& [_,root]:roots)arguments+=L" "+Wide(GuidN(root.at("mountId")))+L" "+Argument(FullPath(Text(root,"rootPath")));Require(arguments.size()<30000);PROCESS_INFORMATION info{};WinCheck(CreateProcessW(executable.c_str(),arguments.data(),nullptr,nullptr,TRUE,CREATE_NO_WINDOW|CREATE_SUSPENDED|EXTENDED_STARTUPINFO_PRESENT,nullptr,ModuleDirectory().c_str(),&startup.StartupInfo,&info));Process.Value=info.hProcess;Handle thread(info.hThread);
        if(!AssignProcessToJobObject(Job.Value,Process.Value)){TerminateProcess(Process.Value,1);throw Failure(HRESULT_FROM_WIN32(GetLastError()));}WinCheck(ResumeThread(thread.Value)!=static_cast<DWORD>(-1));
    }
    ~Child(){if(Job.Value)TerminateJobObject(Job.Value,0);}
};
}
int RunPreviewObserver(const Json& roots)noexcept {
    try{
        Require(roots.is_array()&&!roots.empty()&&roots.size()<=128);SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);ViewEvents events;DirectoryReader reader(roots);
        struct State{Json Value=Json::object();ULONGLONG After{};unsigned Failures{};bool Known{};HRESULT LastError=S_OK;std::string LastStage;};std::map<HWND,State> states;
        std::shared_ptr<DirectoryReader::Request> active;std::future<Json> future;ULONGLONG enumerated{},heartbeat{},samples{},timeouts{},late{},filtered{},directoryMs{},sampleMs{};
        bool expired{},restart{};
        while(true){
            MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
            const auto now=GetTickCount64();
            if(!enumerated||now-enumerated>=5000){
                std::vector<HWND> windows;EnumWindows([](HWND window,LPARAM context)->BOOL{wchar_t name[64]{};GetClassNameW(window,name,64);auto& output=*reinterpret_cast<std::vector<HWND>*>(context);if(output.size()<32&&_wcsicmp(name,L"CabinetWClass")==0&&IsWindowVisible(window)&&!IsIconic(window))output.push_back(window);return TRUE;},reinterpret_cast<LPARAM>(&windows));
                for(auto item=states.begin();item!=states.end();)if(std::find(windows.begin(),windows.end(),item->first)==windows.end()){ManagedWindows.erase(item->first);DirtyWindows.erase(item->first);item=states.erase(item);}else ++item;
                for(auto window:windows){states.try_emplace(window);if(!DirtyWindows.contains(window))DirtyWindows[window]=now-250;}
                enumerated=now;
            }
            if(active){
                if(future.wait_for(std::chrono::milliseconds(0))==std::future_status::ready){
                    auto value=future.get();auto found=states.find(active->Window);if(!expired&&value.value("finishedTick",now)<=active->Deadline&&found!=states.end()){
                        auto& state=found->second;++samples;directoryMs+=value.value("directoryMs",0ULL);sampleMs+=value.value("sampleMs",0ULL);
                        if(value.value("complete",false)){state.Known=true;state.Value=std::move(value);state.Failures=0;state.After=0;state.LastError=S_OK;state.LastStage.clear();if(state.Value.value("owned",false))ManagedWindows.insert(active->Window);else{ManagedWindows.erase(active->Window);++filtered;}}
                        else{state.LastError=value.value("hresult",E_FAIL);state.LastStage=value.value("stage",std::string("viewport"));if(state.LastError==HRESULT_FROM_WIN32(ERROR_TIMEOUT))++timeouts;state.After=now+(++state.Failures>=2?30000:5000);if(state.Value.contains("directory"))state.Value["complete"]=false;}
                    }else ++late;active.reset();expired=false;
                }else if(now>=active->Deadline){
                    if(!expired){expired=true;active->Expired=true;++timeouts;auto found=states.find(active->Window);if(found!=states.end()){auto& state=found->second;state.LastError=HRESULT_FROM_WIN32(ERROR_TIMEOUT);state.LastStage="directory-budget";state.After=now+(++state.Failures>=2?30000:5000);if(state.Value.contains("directory"))state.Value["complete"]=false;}}
                    // 不合作的RPC只有拥有者可结束进程；心跳继续，通知父进程清理且至少30秒后重建。
                    if(now-active->Deadline>=1000)restart=true;
                }
            }
            if(PreviewSamplerUncooperative())restart=true;
            if(!active&&!restart){
                auto selected=DirtyWindows.end();
                for(auto item=DirtyWindows.begin();item!=DirtyWindows.end();){
                    auto found=states.find(item->first);if(found==states.end()){item=DirtyWindows.erase(item);continue;}
                    if(now>=item->second+250&&now>=found->second.After&&(selected==DirtyWindows.end()||item->second<selected->second))selected=item;++item;
                }
                if(selected!=DirtyWindows.end()){active=reader.Submit(selected->first);future=active->Result.get_future();DirtyWindows.erase(selected);}
            }
            if(!heartbeat||now-heartbeat>=250||restart){
                Json views=Json::array(),failures=Json::array();int candidates{};bool complete=!expired;for(auto& [window,state]:states){if(FAILED(state.LastError)&&failures.size()<16)failures.push_back({{"stage",state.LastStage},{"hresult",state.LastError}});if(!state.Known)complete=false;if(state.Value.value("owned",false)&&views.size()<16){auto value=state.Value;if(state.After>now||(active&&active->Window==window&&expired))value["complete"]=false;candidates+=value.value("candidates",0);views.push_back(std::move(value));}}
                ViewDiagnostics={{"strategy","UIA.viewport"},{"windows",states.size()},{"accepted",views.size()},{"candidates",candidates},{"samples",samples},{"timeouts",timeouts},{"late",late},{"filtered",filtered},{"directoryMs",directoryMs},{"sampleMs",sampleMs},{"queued",DirtyWindows.size()},{"active",active?1:0},{"enumerationComplete",complete},{"failures",failures},{"restartRequired",restart}};
                WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),{{"kind","previewViews"},{"views",views},{"diagnostics",ViewDiagnostics}});heartbeat=now;
            }
            Sleep(25);
        }
    }catch(...){return 1;}
}
struct PreviewObserver::Impl {
    RuntimeHost& Host;Handle Stopping{CreateEventW(nullptr,TRUE,FALSE,nullptr)};std::mutex Gate;std::map<std::string,Json> Roots;std::thread Worker;
    explicit Impl(RuntimeHost& host):Host(host),Worker([this]{Run();}){}
    /// <summary>观察进程失败只发布无效样本；保留当前需求，待恢复后确认窗口关闭／目录离开再取消。</summary>
    void CancelDemand()noexcept{try{std::map<std::string,Json> roots;{std::lock_guard lock(Gate);roots=Roots;}for(auto& [session,root]:roots)Host.SendPreview({{"kind","event"},{"mountId",GuidN(root.at("mountId"))},{"sessionId",session},{"event","previewDemand"},{"data",{{"paths",Json::array()},{"complete",false},{"observation",{{"strategy","UIA.viewport"},{"failures",Json::array({{{"stage","observer"},{"hresult",HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE)}}})}}}}}});}catch(...){}}
    void Run()noexcept {
        std::map<std::string,Json> lastDemand;ULONGLONG retry{};while(WaitForSingleObject(Stopping.Value,100)==WAIT_TIMEOUT) {
            {std::lock_guard lock(Gate);if(Roots.empty())continue;}if(GetTickCount64()<retry)continue;
            try {
                std::map<std::string,Json> initial;{std::lock_guard lock(Gate);initial=Roots;}for(auto item=lastDemand.begin();item!=lastDemand.end();)if(!initial.contains(item->first))item=lastDemand.erase(item);else ++item;
                Child child(initial);std::map<std::string,ULONGLONG> lastSample;std::vector<BYTE> buffer;auto heartbeat=GetTickCount64();DWORD expected{};
                while(WaitForSingleObject(Stopping.Value,25)==WAIT_TIMEOUT&&!Host.IsStopping()){
                    std::map<std::string,Json> roots;{std::lock_guard lock(Gate);roots=Roots;}if(roots.empty()||roots!=initial)break;
                    DWORD available{};WinCheck(PeekNamedPipe(child.Output.Value,nullptr,0,nullptr,&available,nullptr));
                    if(!expected&&available>=4){Transfer(child.Output.Value,&expected,4,false);Require(expected>0&&expected<=1024*1024);buffer.clear();buffer.reserve(expected);available-=4;}
                    if(expected&&available){auto count=std::min(available,expected-static_cast<DWORD>(buffer.size()));auto old=buffer.size();buffer.resize(old+count);Transfer(child.Output.Value,buffer.data()+old,count,false);}
                    if(expected&&buffer.size()==expected){
                        auto frame=Parse(std::string(buffer.begin(),buffer.end()),1024*1024);Require(frame.at("kind")=="previewViews"&&frame.at("views").is_array()&&frame.at("views").size()<=16);heartbeat=GetTickCount64();expected=0;
                        Require(!frame.value("diagnostics",Json::object()).value("restartRequired",false),HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                        for(auto& [session,root]:roots)try {
                            auto base=FullPath(Text(root,"rootPath"));Json paths=Json::array();std::set<std::wstring> seen,uncertain;bool complete=frame.value("diagnostics",Json::object()).value("enumerationComplete",true);ValidateRuntimeRoot(root);
                            for(auto& view:frame.at("views")){
                                auto directory=FullPath(Text(view,"directory"));if(!(SamePath(base,directory)||(directory.size()>base.size()&&directory[base.size()]==L'\\'&&SamePath(base,directory.substr(0,base.size())))))continue;
                                if(!view.value("complete",true)){complete=false;uncertain.insert(directory);}Require(view.at("paths").is_array()&&view.at("paths").size()<=64);
                                for(auto& item:view.at("paths")){auto path=FullPath(Wide(item.get<std::string>()));if(path.size()>4096||path.size()<=directory.size()||path[directory.size()]!=L'\\'||!SamePath(directory,path.substr(0,directory.size()))||path.find(L'\\',directory.size()+1)!=std::wstring::npos||!Media(path)||!seen.insert(path).second)continue;if(paths.size()<128)paths.push_back(Utf8(path));}
                            }
                            // 无效视图保留其旧需求，同时接受其他窗口及本轮已验证的命中；不能因一个超时阻塞全部窗口。
                            if(!complete&&lastDemand.contains(session)&&lastDemand.at(session).value("complete",false)){
                                const bool enumeration=frame.value("diagnostics",Json::object()).value("enumerationComplete",true);
                                for(auto& previous:lastDemand.at(session).at("paths")){auto old=FullPath(Wide(previous.get<std::string>()));auto parent=old.substr(0,old.find_last_of(L'\\'));if((!enumeration||uncertain.contains(parent))&&seen.insert(old).second&&paths.size()<128)paths.push_back(previous);}
                                complete=true;
                            }else if(!complete&&!paths.empty())complete=true;
                            Json demand={{"paths",paths},{"complete",complete}};if(lastDemand.contains(session)&&lastDemand.at(session)==demand){
                                const auto sample=frame.value("diagnostics",Json::object()).value("samples",0ULL);if(lastSample[session]!=sample){lastSample[session]=sample;Host.SendPreview({{"kind","event"},{"mountId",GuidN(root.at("mountId"))},{"sessionId",session},{"event","previewDemand"},{"data",{{"paths",Json::array()},{"complete",false},{"observation",frame.value("diagnostics",Json::object())}}}});}continue;
                            }lastDemand[session]=demand;lastSample[session]=frame.value("diagnostics",Json::object()).value("samples",0ULL);
                            Host.SendPreview({{"kind","event"},{"mountId",GuidN(root.at("mountId"))},{"sessionId",session},{"event","previewDemand"},{"data",{{"paths",paths},{"complete",complete},{"observation",frame.value("diagnostics",Json::object())}}}});
                        }catch(...){/* 根刚断开或身份变化时丢弃提示，不能关闭CFAPI通道。 */}
                    }
                    Require(GetTickCount64()-heartbeat<5000,HRESULT_FROM_WIN32(ERROR_TIMEOUT));Require(WaitForSingleObject(child.Process.Value,0)==WAIT_TIMEOUT,HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE));
                }
            }catch(const Failure& error){if(!Host.IsStopping())RecordNativeFailure("NativeRuntime",error.Code,"PreviewObserver");CancelDemand();retry=GetTickCount64()+30000;}
            catch(...){CancelDemand();retry=GetTickCount64()+30000;}
        }
    }
};
PreviewObserver::PreviewObserver(RuntimeHost& host):_impl(std::make_unique<Impl>(host)){}
PreviewObserver::~PreviewObserver(){Stop();}
void PreviewObserver::Configure(const Json& root,bool enabled){std::lock_guard lock(_impl->Gate);auto session=GuidN(root.at("sessionId"));if(enabled)_impl->Roots[session]=root;else _impl->Roots.erase(session);}
void PreviewObserver::Remove(const std::string& session){std::lock_guard lock(_impl->Gate);_impl->Roots.erase(session);}
void PreviewObserver::Stop(){if(!_impl)return;SetEvent(_impl->Stopping.Value);if(_impl->Worker.joinable())_impl->Worker.join();}
}

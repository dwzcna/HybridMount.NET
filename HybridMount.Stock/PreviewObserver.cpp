#include "PreviewObserver.h"
#include "ForegroundPreviewState.h"
#include "VisiblePreviewItems.h"
#include "RuntimeOperations.h"
#include "CloudFileOperations.h"
#include "NativeDiagnostics.h"
#include <exdisp.h>
#include <exdispid.h>
#include <servprov.h>
#include <ocidl.h>
#include <shlwapi.h>
#include <wrl.h>
#include <future>
#include <thread>
#include <mutex>
#include <atomic>
using Microsoft::WRL::ComPtr;
namespace hm {
namespace {
/// <summary>只检查当前前台窗口的本地进程身份；普通应用前台不访问Shell或文件目录。</summary>
PreviewForeground Foreground(HWND& last,PreviewForeground& cached){
    const auto window=GetForegroundWindow();DWORD process{};const auto thread=window?GetWindowThreadProcessId(window,&process):0;
    if(window==last&&(!cached.Window||(cached.Process==process&&cached.Thread==thread)))return cached;
    last=window;cached={};if(!window||!IsWindowVisible(window)||IsIconic(window))return cached;
    wchar_t name[64]{};GetClassNameW(window,name,64);if(_wcsicmp(name,L"CabinetWClass"))return cached;
    DWORD session{},own{};if(!ProcessIdToSessionId(process,&session)||!ProcessIdToSessionId(GetCurrentProcessId(),&own)||session!=own)return cached;
    Handle handle(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,process));if(!handle.Value)return cached;
    wchar_t image[32768]{};DWORD size=32768;FILETIME created{},exit{},kernel{},user{};
    if(!QueryFullProcessImageNameW(handle.Value,0,image,&size)||!GetProcessTimes(handle.Value,&created,&exit,&kernel,&user))return cached;
    const std::wstring path(image,size);if(_wcsicmp(path.substr(path.find_last_of(L'\\')+1).c_str(),L"explorer.exe"))return cached;
    cached={window,process,thread,(static_cast<ULONGLONG>(created.dwHighDateTime)<<32)|created.dwLowDateTime};return cached;
}
class ViewEvents;
thread_local ViewEvents* EventOwner{};
/// <summary>控制线程拥有全部事件钩子；全局只订阅前台变化，其余仅明确事件及前台关联线程。</summary>
class ViewEvents {
    ForegroundPreviewState& State;HWINEVENTHOOK FocusHook{};std::vector<HWINEVENTHOOK> Hooks;HWND Last{},View{};PreviewForeground Cached;bool Navigation{},Content{};
    static void CALLBACK Event(HWINEVENTHOOK,DWORD event,HWND window,LONG object,LONG child,DWORD,DWORD){if(EventOwner)EventOwner->OnEvent(event,window,object,child);}
    void OnEvent(DWORD event,HWND window,LONG object,LONG child)noexcept{
        if(event==EVENT_SYSTEM_FOREGROUND){Last=nullptr;return;}++Events;
        const auto frame=State.Target.Window;
        if(!frame||GetForegroundWindow()!=frame||!window||(window!=frame&&!IsChild(frame,window))){++Ignored;return;}
        const bool inView=View&&(window==View||IsChild(View,window));
        const auto action=ClassifyPreviewEvent(event,object,child,true,true,inView,window==View,State.Value.value("owned",false));
        if(action==PreviewEventAction::Navigation)Navigation=true;else if(action==PreviewEventAction::Content)Content=true;else ++Ignored;
    }
    void Rehook(){
        for(auto hook:Hooks)UnhookWinEvent(hook);Hooks.clear();if(!State.Target.Window)return;
        DWORD pid{};const auto viewThread=View?GetWindowThreadProcessId(View,&pid):0;
        for(const auto thread:{State.Target.Thread,viewThread}){
            if(!thread||(thread==viewThread&&thread==State.Target.Thread&& !Hooks.empty()))continue;
            for(const auto event:{EVENT_OBJECT_SHOW,EVENT_OBJECT_HIDE,EVENT_OBJECT_DESTROY,EVENT_OBJECT_PARENTCHANGE,EVENT_OBJECT_NAMECHANGE,EVENT_OBJECT_VALUECHANGE,EVENT_OBJECT_SELECTION,EVENT_OBJECT_REORDER,EVENT_OBJECT_LOCATIONCHANGE,EVENT_OBJECT_CONTENTSCROLLED,EVENT_SYSTEM_SCROLLINGEND}){
                auto hook=SetWinEventHook(event,event,nullptr,Event,State.Target.Process,thread,WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS);if(hook)Hooks.push_back(hook);
            }
        }
    }
public:
    ULONGLONG Events{},Ignored{};std::shared_ptr<PreviewViewGuard> ActiveGuard;
    explicit ViewEvents(ForegroundPreviewState& state):State(state){EventOwner=this;FocusHook=SetWinEventHook(EVENT_SYSTEM_FOREGROUND,EVENT_SYSTEM_FOREGROUND,nullptr,Event,0,0,WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS);}
    ~ViewEvents(){for(auto hook:Hooks)UnhookWinEvent(hook);if(FocusHook)UnhookWinEvent(FocusHook);EventOwner=nullptr;}
    /// <summary>本地前台检查补偿丢失事件；身份/导航变化同时取消在途请求并立即发布撤销。</summary>
    bool Refresh(ULONGLONG now,bool shellNavigation){
        auto target=Foreground(Last,Cached);if(target.Window&&(!IsWindowVisible(target.Window)||IsIconic(target.Window)))target={};
        const bool focus=State.Focus(target,now);const bool navigated=target.Window&&(Navigation||shellNavigation);
        if(navigated&&!focus)State.Invalidate(now);
        if(focus||navigated){if(ActiveGuard)ActiveGuard->Canceled=true;View=nullptr;Rehook();}
        else if(Content)State.Changed(now);
        Navigation=false;Content=false;return focus||navigated;
    }
    void BindView(HWND view){if(View!=view){View=view;Rehook();}}
};
/// <summary>导航连接点只递增代次/取消当前请求，不在COM通知栈读取目录或执行采样。</summary>
class NavigationSink final:public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,IDispatch>{
    std::function<void()> Changed;
public:
    explicit NavigationSink(std::function<void()> changed):Changed(std::move(changed)){}
    IFACEMETHODIMP GetTypeInfoCount(UINT* count)override{if(!count)return E_POINTER;*count=0;return S_OK;}
    IFACEMETHODIMP GetTypeInfo(UINT,LCID,ITypeInfo**)override{return E_NOTIMPL;}
    IFACEMETHODIMP GetIDsOfNames(REFIID,LPOLESTR*,UINT,LCID,DISPID*)override{return E_NOTIMPL;}
    IFACEMETHODIMP Invoke(DISPID id,REFIID,LCID,WORD,DISPPARAMS*,VARIANT*,EXCEPINFO*,UINT*)override{
        if(id==DISPID_BEFORENAVIGATE2||id==DISPID_NAVIGATECOMPLETE2||id==DISPID_DOCUMENTCOMPLETE)Changed();return S_OK;
    }
};
/// <summary>优先文件系统路径；云目录别名仅接受当前根内单层名称，不读取正文。</summary>
std::optional<std::wstring> ItemPath(IShellItem* item){PWSTR raw{};if(FAILED(item->GetDisplayName(SIGDN_FILESYSPATH,&raw)))return {};std::wstring path(raw);CoTaskMemFree(raw);try{return FullPath(path);}catch(...){return {};}}
/// <summary>Shell接口只属于此STA；缓存前台的全部候选，按可见ShellView消歧，绝不按框架HWND覆盖标签页。</summary>
class DirectoryReader {
public:
    struct Request{PreviewTicket Ticket;std::shared_ptr<PreviewViewGuard> Guard;std::promise<Json> Result;};
    std::atomic<ULONGLONG> Navigation{},Mappings{},Locations{},RootQueries{};
private:
    struct BrowserEntry{HWND Window{};ComPtr<IWebBrowserApp> Browser;};
    struct Binding{ComPtr<IWebBrowserApp> Browser;ComPtr<IShellView> View;ComPtr<IFolderView2> Folder;HWND Window{};};
    Json Roots;Handle Wake{CreateEventW(nullptr,FALSE,FALSE,nullptr)};std::mutex Gate;std::shared_ptr<Request> Pending;bool Closed{};std::thread Worker;
    PreviewForeground Mapped;ULONGLONG MappedGeneration=ULLONG_MAX;std::vector<BrowserEntry> Browsers;
    ComPtr<IConnectionPoint> Connection;DWORD Cookie{};ComPtr<NavigationSink> Sink;std::weak_ptr<PreviewViewGuard> CurrentGuard;
    static void Deadline(Request& request){Require(request.Guard->Current(),HRESULT_FROM_WIN32(ERROR_CANCELLED));Require(GetTickCount64()<request.Ticket.Deadline,HRESULT_FROM_WIN32(ERROR_TIMEOUT));}
    void Unsubscribe(){if(Connection&&Cookie)Connection->Unadvise(Cookie);Cookie=0;Connection.Reset();Sink.Reset();}
    void Subscribe(IWebBrowserApp* browser){
        Unsubscribe();ComPtr<IConnectionPointContainer> container;if(FAILED(browser->QueryInterface(IID_PPV_ARGS(&container)))||FAILED(container->FindConnectionPoint(DIID_DWebBrowserEvents2,&Connection)))return;
        Sink=Microsoft::WRL::Make<NavigationSink>([this]{if(GetForegroundWindow()!=Mapped.Window)return;++Navigation;if(auto guard=CurrentGuard.lock())guard->Canceled=true;});
        if(!Sink||FAILED(Connection->Advise(Sink.Get(),&Cookie)))Unsubscribe();
    }
    void Map(Request& request){
        Deadline(request);Unsubscribe();Browsers.clear();++Mappings;ComPtr<IShellWindows> windows;Check(CoCreateInstance(CLSID_ShellWindows,nullptr,CLSCTX_LOCAL_SERVER,IID_PPV_ARGS(&windows)));
        long count{};Check(windows->get_Count(&count));Deadline(request);
        for(long i=0;i<count&&i<32;++i){
            Deadline(request);VARIANT index{};index.vt=VT_I4;index.lVal=i;ComPtr<IDispatch> dispatch;if(FAILED(windows->Item(index,&dispatch))||!dispatch)continue;
            ComPtr<IWebBrowserApp> browser;if(FAILED(dispatch.As(&browser)))continue;SHANDLE_PTR raw{};if(FAILED(browser->get_HWND(&raw)))continue;
            const auto window=reinterpret_cast<HWND>(raw);DWORD pid{};GetWindowThreadProcessId(window,&pid);
            if(pid==request.Ticket.Target.Process&&GetAncestor(window,GA_ROOT)==request.Ticket.Target.Window)Browsers.push_back({window,std::move(browser)});
        }
        Deadline(request);Mapped=request.Ticket.Target;MappedGeneration=request.Ticket.Generation;
    }
    Binding Browser(Request& request){
        if(Mapped!=request.Ticket.Target||MappedGeneration!=request.Ticket.Generation||Browsers.empty())Map(request);
        auto select=[&]{Binding selected;
            for(auto& entry:Browsers){
                Deadline(request);if(!IsWindow(entry.Window)||!IsWindowVisible(entry.Window))continue;
                ComPtr<IServiceProvider> services;if(FAILED(entry.Browser.As(&services)))continue;ComPtr<IShellBrowser> shell;if(FAILED(services->QueryService(SID_STopLevelBrowser,IID_PPV_ARGS(&shell))))continue;Deadline(request);
                Binding binding;binding.Browser=entry.Browser;if(FAILED(shell->QueryActiveShellView(&binding.View)))continue;Deadline(request);
                if(FAILED(binding.View->GetWindow(&binding.Window))||!IsWindowVisible(binding.Window)||GetAncestor(binding.Window,GA_ROOT)!=request.Ticket.Target.Window)continue;
                if(selected.Browser){if(selected.Browser.Get()==binding.Browser.Get())continue;throw Failure(HRESULT_FROM_WIN32(ERROR_DUP_NAME),"ambiguous-tab");}
                Check(binding.View.As(&binding.Folder));selected=std::move(binding);
            }return selected;};
        auto result=select();if(!result.Browser&&!Browsers.empty()){Map(request);result=select();}
        Require(result.Browser!=nullptr,HRESULT_FROM_WIN32(ERROR_NOT_FOUND));if(!Connection)Subscribe(result.Browser.Get());return result;
    }
    const Json* Managed(const std::wstring& directory)const{
        for(const auto& root:Roots){const auto base=FullPath(Text(root,"rootPath"));if(SamePath(base,directory)||(directory.size()>base.size()&&directory[base.size()]==L'\\'&&SamePath(base,directory.substr(0,base.size()))))return &root;}return nullptr;
    }
    Json Inspect(Request& request){
        const auto started=GetTickCount64();const char* stage="mapping";Json result={{"owned",false},{"complete",false},{"paths",Json::array()}};
        try{
            Deadline(request);auto binding=Browser(request);Deadline(request);request.Guard->View=binding.Window;result["viewWindow"]=reinterpret_cast<ULONG_PTR>(binding.Window);
            stage="location";++Locations;std::optional<std::wstring> directory;BSTR raw{};const auto location=binding.Browser->get_LocationURL(&raw);std::wstring url;
            if(SUCCEEDED(location)&&raw)url.assign(raw,SysStringLen(raw));if(raw)SysFreeString(raw);Deadline(request);
            if(url.starts_with(L"file:///")){std::wstring path(32768,L'\0');DWORD length=static_cast<DWORD>(path.size());if(SUCCEEDED(PathCreateFromUrlW(url.c_str(),path.data(),&length,0))){path.resize(wcslen(path.c_str()));try{directory=FullPath(path);}catch(...){result["complete"]=true;result["filtered"]=true;return result;}}}
            if(!directory){
                stage="directory";ComPtr<IShellFolder> folder;Check(binding.Folder->GetFolder(IID_PPV_ARGS(&folder)));Deadline(request);ComPtr<IPersistFolder2> persist;Check(folder.As(&persist));PIDLIST_ABSOLUTE pidl{};Check(persist->GetCurFolder(&pidl));
                struct Pidl{PIDLIST_ABSOLUTE Value;~Pidl(){CoTaskMemFree(Value);}} owner{pidl};ComPtr<IShellItem> item;Check(SHCreateItemFromIDList(pidl,IID_PPV_ARGS(&item)));directory=ItemPath(item.Get());Deadline(request);
                if(!directory){ComPtr<IPersistFolder3> target;PERSIST_FOLDER_TARGET_INFO info{};
                    if(SUCCEEDED(folder.As(&target))&&SUCCEEDED(target->GetFolderTargetInfo(&info))){Pidl targetOwner{info.pidlTargetFolder};if(info.pidlTargetFolder){ComPtr<IShellItem> targetItem;if(SUCCEEDED(SHCreateItemFromIDList(info.pidlTargetFolder,IID_PPV_ARGS(&targetItem))))directory=ItemPath(targetItem.Get());}if(!directory&&info.szTargetParsingName[0])try{directory=FullPath(info.szTargetParsingName);}catch(...) {}}
                }
            }
            Deadline(request);if(!directory||!Managed(*directory)){result["complete"]=true;result["filtered"]=true;result["directoryMs"]=GetTickCount64()-started;result["totalMs"]=GetTickCount64()-started;return result;}
            stage="root";++RootQueries;const auto marker=RootMarker(*directory);Deadline(request);Require(GuidN(marker.at("MountId"))==GuidN(Managed(*directory)->at("mountId")),E_ACCESSDENIED);
            result["owned"]=true;result["directory"]=Utf8(*directory);result["directoryMs"]=GetTickCount64()-started;
            stage="viewport";const auto sample=ReadVisiblePreviewItems(binding.View.Get(),binding.Folder.Get(),*directory,request.Ticket.Deadline,request.Guard);Deadline(request);Require(IsWindowVisible(binding.Window),HRESULT_FROM_WIN32(ERROR_CANCELLED));
            // 同一ShellView可能被导航复用；采样后再核对URL，拒绝把新目录的名称拼到旧目录。
            BSTR afterRaw{};const auto afterStatus=binding.Browser->get_LocationURL(&afterRaw);std::wstring afterUrl;if(SUCCEEDED(afterStatus)&&afterRaw)afterUrl.assign(afterRaw,SysStringLen(afterRaw));if(afterRaw)SysFreeString(afterRaw);++Locations;Deadline(request);
            if(FAILED(afterStatus)||afterUrl!=url){++Navigation;request.Guard->Canceled=true;throw Failure(HRESULT_FROM_WIN32(ERROR_CANCELLED),"navigation-changed");}
            result["complete"]=sample.Complete;result["paths"]=sample.Paths;result["candidates"]=sample.Candidates;result["sampleMs"]=sample.Milliseconds;result["hresult"]=sample.Error;
        }catch(const Failure& error){result["hresult"]=error.Code;result["stage"]=error.Stage==std::string("operation")?stage:error.Stage;}catch(...){result["hresult"]=E_FAIL;result["stage"]=stage;}
        result["totalMs"]=GetTickCount64()-started;return result;
    }
    void Run()noexcept{
        const auto initialized=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        while(true){
            MsgWaitForMultipleObjects(1,&Wake.Value,FALSE,100,QS_ALLINPUT);MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
            std::shared_ptr<Request> request;{std::lock_guard lock(Gate);if(Closed)break;request=std::move(Pending);}if(!request)continue;CurrentGuard=request->Guard;
            Json result=SUCCEEDED(initialized)?Inspect(*request):Json{{"owned",false},{"complete",false},{"hresult",initialized}};
            result["finishedTick"]=GetTickCount64();try{request->Result.set_value(std::move(result));}catch(...){}CurrentGuard.reset();
        }
        Unsubscribe();Browsers.clear();if(SUCCEEDED(initialized))CoUninitialize();
    }
public:
    explicit DirectoryReader(Json roots):Roots(std::move(roots)){WinCheck(Wake.Value!=nullptr);Worker=std::thread([this]{Run();});}
    ~DirectoryReader(){{std::lock_guard lock(Gate);Closed=true;}SetEvent(Wake.Value);if(Worker.joinable())Worker.join();}
    std::shared_ptr<Request> Submit(PreviewTicket ticket){auto request=std::make_shared<Request>();request->Ticket=ticket;request->Guard=std::make_shared<PreviewViewGuard>();request->Guard->Frame=ticket.Target.Window;request->Guard->Process=ticket.Target.Process;{std::lock_guard lock(Gate);Require(!Pending);Pending=request;}SetEvent(Wake.Value);return request;}
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
        Require(roots.is_array()&&!roots.empty()&&roots.size()<=128);SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        ForegroundPreviewState state;ViewEvents events(state);DirectoryReader reader(roots);
        std::shared_ptr<DirectoryReader::Request> active;std::future<Json> future;ULONGLONG heartbeat{},navigation{};bool expired{},restart{},publish=true;
        while(true){
            MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
            const auto now=GetTickCount64();const auto version=reader.Navigation.load();if(events.Refresh(now,version!=navigation))publish=true;navigation=version;
            if(active){
                if(future.wait_for(std::chrono::milliseconds(0))==std::future_status::ready){
                    auto value=future.get();if(!expired&&active->Guard->Current()){if(state.Complete(active->Ticket,std::move(value),now)){
                        if(state.Value.contains("viewWindow"))events.BindView(reinterpret_cast<HWND>(state.Value.at("viewWindow").get<ULONG_PTR>()));publish=true;}
                    }else{++state.Late;if(!expired&&active->Ticket.Generation==state.Generation){state.Invalidate(now);publish=true;}}active.reset();events.ActiveGuard.reset();expired=false;
                }else if(now>=active->Ticket.Deadline){
                    if(!expired){expired=true;active->Guard->Canceled=true;if(active->Ticket.Generation==state.Generation){state.Failed(now,HRESULT_FROM_WIN32(ERROR_TIMEOUT),"directory-budget");publish=true;}}
                    if(now-active->Ticket.Deadline>=1000)restart=true;
                }
            }
            if(PreviewSamplerUncooperative())restart=true;
            if(!active&&!restart)if(auto ticket=state.Take(now)){active=reader.Submit(*ticket);events.ActiveGuard=active->Guard;future=active->Result.get_future();}
            if(publish||!heartbeat||now-heartbeat>=250||restart){
                auto frame=state.Frame();auto& diagnostics=frame["diagnostics"];diagnostics["events"]=events.Events;diagnostics["ignoredEvents"]=events.Ignored;diagnostics["mappings"]=reader.Mappings.load();diagnostics["locationQueries"]=reader.Locations.load();diagnostics["rootQueries"]=reader.RootQueries.load();diagnostics["active"]=active?1:0;diagnostics["restartRequired"]=restart;
                if(FAILED(state.LastError))diagnostics["failures"]=Json::array({{{"stage",state.LastStage},{"hresult",state.LastError}}});else diagnostics["failures"]=Json::array();
                if(!publish){frame["kind"]="previewHeartbeat";frame.erase("views");}
                WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),frame);heartbeat=now;publish=false;
            }
            Sleep(25);
        }
    }catch(...){return 1;}
}
struct PreviewObserver::Impl {
    RuntimeHost& Host;Handle Stopping{CreateEventW(nullptr,TRUE,FALSE,nullptr)};std::mutex Gate;PreviewDemandPublisher::Roots Roots;Json Observation={{"scope","foreground"},{"running",false}};std::thread Worker;
    explicit Impl(RuntimeHost& host):Host(host),Worker([this]{Run();}){}
    /// <summary>父进程只消费需求变化；心跳仅存活。当前前台丢失时不用Shell/根查询即可撤销旧需求。</summary>
    void Run()noexcept {
        PreviewDemandPublisher publisher;ULONGLONG retry{};HWND foreground{},view{};DWORD foregroundProcess{};Json observation=Json::object();
        auto send=[&](const std::string& session,const Json& root,const Json& paths){auto diagnostics=observation;diagnostics["parentRootChecks"]=publisher.RootChecks;Host.SendPreview({{"kind","event"},{"mountId",GuidN(root.at("mountId"))},{"sessionId",session},{"event","previewDemand"},{"data",{{"paths",paths},{"complete",true},{"observation",diagnostics}}}});};
        auto current=[&]{DWORD pid{};return foreground&&GetForegroundWindow()==foreground&&IsWindowVisible(foreground)&&!IsIconic(foreground)&&GetWindowThreadProcessId(foreground,&pid)&&pid==foregroundProcess&&(!view||IsWindowVisible(view));};
        while(WaitForSingleObject(Stopping.Value,100)==WAIT_TIMEOUT&&!Host.IsStopping()){
            PreviewDemandPublisher::Roots initial;{std::lock_guard lock(Gate);initial=Roots;}
            if(foreground&&!current()){publisher.Clear(initial,send);foreground=nullptr;view=nullptr;}
            if(initial.empty()||GetTickCount64()<retry){std::lock_guard lock(Gate);Observation["running"]=false;continue;}
            try{
                Child child(initial);std::vector<BYTE> buffer;auto heartbeat=GetTickCount64();DWORD expected{};
                while(WaitForSingleObject(Stopping.Value,25)==WAIT_TIMEOUT&&!Host.IsStopping()){
                    PreviewDemandPublisher::Roots roots;{std::lock_guard lock(Gate);roots=Roots;}
                    if(roots.empty()||roots!=initial){publisher.Clear(initial,send);foreground=nullptr;view=nullptr;break;}
                    if(foreground&&!current()){publisher.Clear(roots,send);foreground=nullptr;view=nullptr;}
                    DWORD available{};WinCheck(PeekNamedPipe(child.Output.Value,nullptr,0,nullptr,&available,nullptr));
                    if(!expected&&available>=4){Transfer(child.Output.Value,&expected,4,false);Require(expected>0&&expected<=1024*1024);buffer.clear();buffer.reserve(expected);available-=4;}
                    if(expected&&available){auto count=std::min(available,expected-static_cast<DWORD>(buffer.size()));auto old=buffer.size();buffer.resize(old+count);Transfer(child.Output.Value,buffer.data()+old,count,false);}
                    if(expected&&buffer.size()==expected){
                        auto frame=Parse(std::string(buffer.begin(),buffer.end()),1024*1024);const auto kind=frame.at("kind").get<std::string>();Require(kind=="previewViews"||kind=="previewHeartbeat");heartbeat=GetTickCount64();expected=0;
                        observation=frame.value("diagnostics",Json::object());{std::lock_guard lock(Gate);Observation=observation;Observation["running"]=true;Observation["parentRootChecks"]=publisher.RootChecks;Observation["publications"]=publisher.Publications;}Require(!observation.value("restartRequired",false),HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                        if(kind=="previewViews"){
                            const auto target=reinterpret_cast<HWND>(frame.value("foreground",static_cast<ULONG_PTR>(0)));const auto process=frame.value("foregroundProcess",0UL);DWORD actual{};
                            if(target&&(GetForegroundWindow()!=target||!GetWindowThreadProcessId(target,&actual)||actual!=process)){publisher.Clear(roots,send);continue;}
                            publisher.Process(frame,roots,[](const Json& root){ValidateRuntimeRoot(root);},send);
                            foreground=target;foregroundProcess=process;view=nullptr;
                            if(frame.at("views").size()==1)view=reinterpret_cast<HWND>(frame.at("views")[0].value("viewWindow",static_cast<ULONG_PTR>(0)));
                        }
                    }
                    Require(GetTickCount64()-heartbeat<5000,HRESULT_FROM_WIN32(ERROR_TIMEOUT));Require(WaitForSingleObject(child.Process.Value,0)==WAIT_TIMEOUT,HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE));
                }
            }catch(const Failure& error){if(!Host.IsStopping())RecordNativeFailure("NativeRuntime",error.Code,"PreviewObserver");retry=GetTickCount64()+30000;}
            catch(...){retry=GetTickCount64()+30000;}
        }
    }
};
PreviewObserver::PreviewObserver(RuntimeHost& host):_impl(std::make_unique<Impl>(host)){}
PreviewObserver::~PreviewObserver(){Stop();}
void PreviewObserver::Configure(const Json& root,bool enabled){std::lock_guard lock(_impl->Gate);auto session=GuidN(root.at("sessionId"));if(enabled)_impl->Roots[session]=root;else _impl->Roots.erase(session);}
void PreviewObserver::Remove(const std::string& session){std::lock_guard lock(_impl->Gate);_impl->Roots.erase(session);}
Json PreviewObserver::Statistics()const{std::lock_guard lock(_impl->Gate);return _impl->Observation;}
void PreviewObserver::Stop(){if(!_impl)return;SetEvent(_impl->Stopping.Value);if(_impl->Worker.joinable())_impl->Worker.join();}
#ifdef HYBRIDMOUNT_NATIVE_TESTS
/// <summary>仅隔离映像：注入时钟和窗口证据，复用生产策略及发布器；不安装钩子、不访问用户目录。</summary>
Json TestForegroundObserver(const Json& input){
    ForegroundPreviewState state;PreviewDemandPublisher publisher;PreviewDemandPublisher::Roots roots;
    for(const auto& item:input.value("roots",Json::array()))roots[item.at("session").get<std::string>()]=item;
    Json outputs=Json::array(),sent=Json::array();std::vector<PreviewTicket> tickets;
    auto send=[&](const std::string& session,const Json&,const Json& paths){sent.push_back({{"session",session},{"paths",paths}});};
    auto verify=[](const Json& root){Require(!root.value("reject",false),E_ACCESSDENIED);};
    for(const auto& step:input.at("steps")){
        const auto now=step.value("now",0ULL);const auto op=step.at("op").get<std::string>();Json output;
        if(op=="focus")state.Focus({reinterpret_cast<HWND>(step.value("window",static_cast<ULONG_PTR>(0))),step.value("process",1UL),step.value("thread",2UL),step.value("created",3ULL)},now);
        else if(op=="navigate")state.Invalidate(now);
        else if(op=="change")state.Changed(now);
        else if(op=="event"){
            const auto action=ClassifyPreviewEvent(step.at("event").get<DWORD>(),step.value("object",static_cast<LONG>(OBJID_CLIENT)),step.value("child",0L),step.value("current",true),step.value("related",true),step.value("inView",false),step.value("viewWindow",false),state.Value.value("owned",false));
            if(action==PreviewEventAction::Navigation)state.Invalidate(now);else if(action==PreviewEventAction::Content)state.Changed(now);output["action"]=static_cast<int>(action);
        }
        else if(op=="take"){auto ticket=state.Take(now);output["taken"]=ticket.has_value();if(ticket){output["ticket"]=tickets.size();tickets.push_back(*ticket);}}
        else if(op=="complete"){auto index=step.at("ticket").get<size_t>();Require(index<tickets.size());output["accepted"]=state.Complete(tickets[index],step.at("result"),now);}
        else if(op=="publish")publisher.Process(state.Frame(),roots,verify,send);
        else if(op=="frame")publisher.Process(step.at("frame"),roots,verify,send);
        else if(op=="clear")publisher.Clear(roots,send);
        else Require(op=="snapshot");
        output["generation"]=state.Generation;output["due"]=state.Due;output["after"]=state.After;output["late"]=state.Late;output["timeouts"]=state.Timeouts;output["dirty"]=state.Dirty;output["value"]=state.Value;output["rootChecks"]=publisher.RootChecks;output["publications"]=publisher.Publications;output["frame"]=state.Frame();outputs.push_back(std::move(output));
    }
    return {{"outputs",outputs},{"sent",sent},{"rootChecks",publisher.RootChecks},{"publications",publisher.Publications}};
}
#endif

}

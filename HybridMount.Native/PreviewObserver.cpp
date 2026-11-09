#include "PreviewObserver.h"
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
using Microsoft::WRL::ComPtr;
namespace hm {
namespace {
thread_local Json ViewDiagnostics=Json::object();
bool Media(const std::wstring& path){auto dot=path.find_last_of(L'.');if(dot==std::wstring::npos)return false;auto extension=path.substr(dot);for(auto& letter:extension)letter=static_cast<wchar_t>(towlower(letter));for(auto item:{L".jpg",L".jpeg",L".png",L".bmp",L".gif",L".tif",L".tiff",L".heic",L".heif",L".webp",L".avif",L".mp4",L".exe"})if(extension==item)return true;return false;}
/// <summary>优先真实文件系统路径；侧栏别名下的项目可退回已核验目录内的单一文件名，不接收任意相对路径。</summary>
std::optional<std::wstring> ItemPath(IShellItem* item,const std::wstring& directory=L""){PWSTR raw{};if(FAILED(item->GetDisplayName(SIGDN_FILESYSPATH,&raw))){if(directory.empty()||FAILED(item->GetDisplayName(SIGDN_PARENTRELATIVEPARSING,&raw)))return {};std::wstring name(raw);CoTaskMemFree(raw);if(name.empty()||name==L"."||name==L".."||name.find_first_of(L"\\/:")!=std::wstring::npos)return {};try{return FullPath(directory+L"\\"+name);}catch(...){return {};}}std::wstring path(raw);CoTaskMemFree(raw);try{return FullPath(path);}catch(...){return {};}}
Json Views(const std::set<std::string>& mounts){
    Json result=Json::array();ViewDiagnostics={{"windows",0},{"accepted",0},{"visible",0},{"session",0},{"process",0},{"explorer",0},{"failures",Json::array()}};if(mounts.empty()||!GetShellWindow())return result;ComPtr<IShellWindows> windows;Check(CoCreateInstance(CLSID_ShellWindows,nullptr,CLSCTX_LOCAL_SERVER,IID_PPV_ARGS(&windows)));long count{};Check(windows->get_Count(&count));ViewDiagnostics["windows"]=count;int total{};const char* stage="window";
    for(long i=0;i<count&&i<32&&result.size()<16;++i)try {
        stage="dispatch";VARIANT index{};index.vt=VT_I4;index.lVal=i;ComPtr<IDispatch> dispatch;Check(windows->Item(index,&dispatch));if(!dispatch)continue;
        ComPtr<IWebBrowserApp> browser;Check(dispatch.As(&browser));SHANDLE_PTR rawWindow{};Check(browser->get_HWND(&rawWindow));auto window=reinterpret_cast<HWND>(rawWindow);if(!IsWindowVisible(window)||IsIconic(window))continue;ViewDiagnostics["visible"]=ViewDiagnostics["visible"].get<int>()+1;
        DWORD pid{},session{},current{};GetWindowThreadProcessId(window,&pid);if(!ProcessIdToSessionId(pid,&session)||!ProcessIdToSessionId(GetCurrentProcessId(),&current)||session!=current)continue;ViewDiagnostics["session"]=ViewDiagnostics["session"].get<int>()+1;
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid));if(!process.Value)continue;wchar_t image[32768]{};DWORD size=32768;if(!QueryFullProcessImageNameW(process.Value,0,image,&size))continue;ViewDiagnostics["process"]=ViewDiagnostics["process"].get<int>()+1;std::wstring imagePath(image,size);if(_wcsicmp(imagePath.substr(imagePath.find_last_of(L'\\')+1).c_str(),L"explorer.exe"))continue;ViewDiagnostics["explorer"]=ViewDiagnostics["explorer"].get<int>()+1;
        stage="view";ComPtr<IServiceProvider> services;Check(dispatch.As(&services));ComPtr<IShellBrowser> shell;Check(services->QueryService(SID_STopLevelBrowser,IID_PPV_ARGS(&shell)));ComPtr<IShellView> view;Check(shell->QueryActiveShellView(&view));ComPtr<IFolderView2> folderView;Check(view.As(&folderView));
        stage="folder";ComPtr<IShellFolder> folder;Check(folderView->GetFolder(IID_PPV_ARGS(&folder)));ComPtr<IPersistFolder2> persist;Check(folder.As(&persist));PIDLIST_ABSOLUTE root{};Check(persist->GetCurFolder(&root));struct Pidl{PIDLIST_ABSOLUTE Value;~Pidl(){CoTaskMemFree(Value);}} owner{root};
        stage="directory";ComPtr<IShellItem> rootItem;Check(SHCreateItemFromIDList(root,IID_PPV_ARGS(&rootItem)));auto directory=ItemPath(rootItem.Get());
        if(!directory){ComPtr<IPersistFolder3> target;PERSIST_FOLDER_TARGET_INFO info{};if(SUCCEEDED(folder.As(&target))&&SUCCEEDED(target->GetFolderTargetInfo(&info))){Pidl targetOwner{info.pidlTargetFolder};if(info.pidlTargetFolder){ComPtr<IShellItem> item;if(SUCCEEDED(SHCreateItemFromIDList(info.pidlTargetFolder,IID_PPV_ARGS(&item))))directory=ItemPath(item.Get());}if(!directory&&info.szTargetParsingName[0])try{directory=FullPath(info.szTargetParsingName);}catch(...){}}}
        // 文件系统PIDL可被云侧栏别名包裹；只接受本地file:///地址，随后仍须核验同步根身份。
        if(!directory){BSTR location{};if(SUCCEEDED(browser->get_LocationURL(&location))&&location){std::wstring url(location,SysStringLen(location));SysFreeString(location);if(url.starts_with(L"file:///")){std::wstring decoded(32768,L'\0');DWORD length=static_cast<DWORD>(decoded.size());if(SUCCEEDED(PathCreateFromUrlW(url.c_str(),decoded.data(),&length,0)))try{decoded.resize(wcslen(decoded.c_str()));directory=FullPath(decoded);}catch(...){}}}}
        ViewDiagnostics["directoryAvailable"]=directory.has_value();if(!directory||directory->size()>4096)continue;stage="root";auto marker=RootMarker(*directory);if(!mounts.contains(GuidN(marker.at("MountId"))))continue;
        ViewDiagnostics["accepted"]=ViewDiagnostics["accepted"].get<int>()+1;stage="visible";Json paths=Json::array();int start=-1;
        while(paths.size()<64&&total<128) {
            int visible{};if(FAILED(folderView->GetVisibleItem(start,FALSE,&visible))||visible<=start)break;start=visible;PITEMID_CHILD child{};if(FAILED(folderView->Item(visible,&child)))continue;Pidl childOwner{child};
            ComPtr<IShellItem> item;if(FAILED(SHCreateItemWithParent(root,folder.Get(),child,IID_PPV_ARGS(&item))))continue;auto path=ItemPath(item.Get(),*directory);if(!path||path->size()>4096||!Media(*path))continue;
            paths.push_back(Utf8(*path));++total;
        }
        result.push_back({{"directory",Utf8(*directory)},{"paths",std::move(paths)}});
    }catch(const Failure& error){if(ViewDiagnostics["failures"].size()<16)ViewDiagnostics["failures"].push_back({{"stage",stage},{"hresult",error.Code}});}catch(...){/* 一个关闭中或非文件系统窗口不能阻止其他视图。 */}
    return result;
}
struct Child {
    Handle Job,Process,Output;
    explicit Child(const std::map<std::string,Json>& roots){
        Handle write;SECURITY_ATTRIBUTES security{sizeof(security),nullptr,TRUE};WinCheck(CreatePipe(&Output.Value,&write.Value,&security,0));WinCheck(SetHandleInformation(Output.Value,HANDLE_FLAG_INHERIT,0));
        SIZE_T size{};InitializeProcThreadAttributeList(nullptr,1,0,&size);std::vector<BYTE> memory(size);auto attributes=reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(memory.data());WinCheck(InitializeProcThreadAttributeList(attributes,1,0,&size));struct Attributes{LPPROC_THREAD_ATTRIBUTE_LIST Value;~Attributes(){DeleteProcThreadAttributeList(Value);}} owner{attributes};
        HANDLE inherited[]{write.Value};WinCheck(UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,inherited,sizeof(inherited),nullptr,nullptr));STARTUPINFOEXW startup{};startup.StartupInfo.cb=sizeof(startup);startup.StartupInfo.dwFlags=STARTF_USESTDHANDLES;startup.StartupInfo.hStdOutput=write.Value;startup.lpAttributeList=attributes;
        Job.Value=CreateJobObjectW(nullptr,nullptr);WinCheck(Job.Value!=nullptr);JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE|JOB_OBJECT_LIMIT_PROCESS_MEMORY;limits.ProcessMemoryLimit=128ULL*1024*1024;WinCheck(SetInformationJobObject(Job.Value,JobObjectExtendedLimitInformation,&limits,sizeof(limits)));
        auto executable=ModuleDirectory()+L"\\HybridMount.Native.exe",arguments=L"\""+executable+L"\" --preview-observer";for(auto& [_,root]:roots)arguments+=L" "+Wide(GuidN(root.at("mountId")));PROCESS_INFORMATION info{};WinCheck(CreateProcessW(executable.c_str(),arguments.data(),nullptr,nullptr,TRUE,CREATE_NO_WINDOW|CREATE_SUSPENDED|EXTENDED_STARTUPINFO_PRESENT,nullptr,ModuleDirectory().c_str(),&startup.StartupInfo,&info));Process.Value=info.hProcess;Handle thread(info.hThread);
        if(!AssignProcessToJobObject(Job.Value,Process.Value)){TerminateProcess(Process.Value,1);throw Failure(HRESULT_FROM_WIN32(GetLastError()));}WinCheck(ResumeThread(thread.Value)!=static_cast<DWORD>(-1));
    }
    ~Child(){if(Job.Value)TerminateJobObject(Job.Value,0);}
};
}
int RunPreviewObserver(const std::vector<std::string>& mounts)noexcept {
    const auto initialized=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);struct Apartment{HRESULT Code;~Apartment(){if(SUCCEEDED(Code))CoUninitialize();}} apartment{initialized};
    try{Check(initialized);std::set<std::string> allowed(mounts.begin(),mounts.end());while(true){auto views=Views(allowed);WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),{{"kind","previewViews"},{"views",views},{"diagnostics",ViewDiagnostics}});auto until=GetTickCount64()+1000;while(GetTickCount64()<until){MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}Sleep(25);}}}
    catch(...){return 1;}
}
struct PreviewObserver::Impl {
    RuntimeHost& Host;Handle Stopping{CreateEventW(nullptr,TRUE,FALSE,nullptr)};std::mutex Gate;std::map<std::string,Json> Roots;std::thread Worker;
    explicit Impl(RuntimeHost& host):Host(host),Worker([this]{Run();}){}
    void Run()noexcept {
        ULONGLONG retry{};while(WaitForSingleObject(Stopping.Value,100)==WAIT_TIMEOUT) {
            {std::lock_guard lock(Gate);if(Roots.empty())continue;}if(GetTickCount64()<retry)continue;
            try {
                std::map<std::string,Json> initial;{std::lock_guard lock(Gate);initial=Roots;}Child child(initial);std::vector<BYTE> buffer;auto heartbeat=GetTickCount64();DWORD expected{};
                while(WaitForSingleObject(Stopping.Value,25)==WAIT_TIMEOUT&&!Host.IsStopping()){
                    std::map<std::string,Json> roots;{std::lock_guard lock(Gate);roots=Roots;}if(roots.empty()||roots!=initial)break;
                    DWORD available{};WinCheck(PeekNamedPipe(child.Output.Value,nullptr,0,nullptr,&available,nullptr));
                    if(!expected&&available>=4){Transfer(child.Output.Value,&expected,4,false);Require(expected>0&&expected<=1024*1024);buffer.clear();buffer.reserve(expected);available-=4;}
                    if(expected&&available){auto count=std::min(available,expected-static_cast<DWORD>(buffer.size()));auto old=buffer.size();buffer.resize(old+count);Transfer(child.Output.Value,buffer.data()+old,count,false);}
                    if(expected&&buffer.size()==expected){
                        auto frame=Parse(std::string(buffer.begin(),buffer.end()),1024*1024);Require(frame.at("kind")=="previewViews"&&frame.at("views").is_array()&&frame.at("views").size()<=16);heartbeat=GetTickCount64();expected=0;
                        for(auto& [session,root]:roots)try {
                            auto base=FullPath(Text(root,"rootPath"));Json paths=Json::array();std::set<std::wstring> seen;ValidateRuntimeRoot(root);
                            for(auto& view:frame.at("views")){
                                auto directory=FullPath(Text(view,"directory"));if(!(SamePath(base,directory)||(directory.size()>base.size()&&directory[base.size()]==L'\\'&&SamePath(base,directory.substr(0,base.size())))))continue;
                                Require(view.at("paths").is_array()&&view.at("paths").size()<=64);
                                for(auto& item:view.at("paths")){auto path=FullPath(Wide(item.get<std::string>()));if(path.size()>4096||path.size()<=directory.size()||path[directory.size()]!=L'\\'||!SamePath(directory,path.substr(0,directory.size()))||path.find(L'\\',directory.size()+1)!=std::wstring::npos||!Media(path)||!seen.insert(path).second)continue;if(paths.size()<128)paths.push_back(Utf8(path));}
                            }
                            Host.SendPreview({{"kind","event"},{"mountId",GuidN(root.at("mountId"))},{"sessionId",session},{"event","previewDemand"},{"data",{{"paths",paths},{"observation",frame.value("diagnostics",Json::object())}}}});
                        }catch(...){/* 根刚断开或身份变化时丢弃提示，不能关闭CFAPI通道。 */}
                    }
                    Require(GetTickCount64()-heartbeat<5000,HRESULT_FROM_WIN32(ERROR_TIMEOUT));Require(WaitForSingleObject(child.Process.Value,0)==WAIT_TIMEOUT,HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE));
                }
            }catch(const Failure& error){if(!Host.IsStopping())RecordNativeFailure("NativeRuntime",error.Code,"PreviewObserver");retry=GetTickCount64()+10000;}
            catch(...){retry=GetTickCount64()+10000;}
        }
    }
};
PreviewObserver::PreviewObserver(RuntimeHost& host):_impl(std::make_unique<Impl>(host)){}
PreviewObserver::~PreviewObserver(){Stop();}
void PreviewObserver::Configure(const Json& root,bool enabled){std::lock_guard lock(_impl->Gate);auto session=GuidN(root.at("sessionId"));if(enabled)_impl->Roots[session]=root;else _impl->Roots.erase(session);}
void PreviewObserver::Remove(const std::string& session){std::lock_guard lock(_impl->Gate);_impl->Roots.erase(session);}
void PreviewObserver::Stop(){if(!_impl)return;SetEvent(_impl->Stopping.Value);if(_impl->Worker.joinable())_impl->Worker.join();}
}

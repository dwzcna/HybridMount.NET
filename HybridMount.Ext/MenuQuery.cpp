#include "MenuQuery.h"
#include "../HybridMount.Stock/NativeDiagnostics.h"
#include <condition_variable>
#include <deque>
#include <functional>
#include <mutex>
#include <thread>
#include <algorithm>
using namespace Microsoft::WRL;
namespace hm::menu {
std::array<std::atomic<unsigned long long>,14> Counters{};
namespace {
std::atomic<DWORD> TestDelay{};
/// <summary>Shell数组可按值封送，接口地址不能作为跨进程选择身份；最多读取16个已有PIDL作观察签名。</summary>
struct SelectionKey {
    ComPtr<IUnknown> Identity,Site;DWORD Count{};unsigned long long Hash=1469598103934665603ULL;bool Pidls{},Valid=true;
    bool Same(const SelectionKey& other)const{return Valid&&other.Valid&&Count==other.Count&&Site.Get()==other.Site.Get()&&((Pidls&&other.Pidls)?Hash==other.Hash:!Pidls&&!other.Pidls&&Identity.Get()==other.Identity.Get());}
};
SelectionKey Key(IShellItemArray* selection,IUnknown* site){
    SelectionKey key;if(site&&FAILED(site->QueryInterface(IID_PPV_ARGS(&key.Site)))){key.Valid=false;return key;}
    if(!selection)return key;if(FAILED(selection->QueryInterface(IID_PPV_ARGS(&key.Identity)))||FAILED(selection->GetCount(&key.Count))||key.Count>1024){key.Valid=false;return key;}
    key.Pidls=true;size_t total{};
    for(DWORD i=0;i<std::min<DWORD>(key.Count,16);++i){
        ComPtr<IShellItem> item;PIDLIST_ABSOLUTE pidl{};
        if(FAILED(selection->GetItemAt(i,&item))||FAILED(SHGetIDListFromObject(item.Get(),&pidl))||!pidl){key.Pidls=false;break;}
        const auto size=ILGetSize(pidl);if(size>128*1024||(total+=size)>1024*1024){CoTaskMemFree(pidl);key.Pidls=false;break;}
        for(UINT offset=0;offset<size;++offset){key.Hash^=reinterpret_cast<const BYTE*>(pidl)[offset];key.Hash*=1099511628211ULL;}key.Hash^=i;key.Hash*=1099511628211ULL;CoTaskMemFree(pidl);
    }
    return key;
}
struct Request {std::atomic<bool> Canceled{},Ready{};std::shared_ptr<const Summary> Value;ULONGLONG Completed{},Generation{};bool Actions{};};
/// <summary>每份封送流只领取一次；取消的排队项也须在COM线程领取并归还其引用。</summary>
struct Input {
    IStream* Selection{};IStream* Site{};
    HRESULT Capture(IShellItemArray* selection,IUnknown* site){auto code=selection?CoMarshalInterThreadInterfaceInStream(__uuidof(IShellItemArray),selection,&Selection):S_OK;if(SUCCEEDED(code)&&site)code=CoMarshalInterThreadInterfaceInStream(IID_IUnknown,site,&Site);return code;}
    HRESULT Take(ComPtr<IShellItemArray>& selection,ComPtr<IUnknown>& site){auto code=Selection?CoGetInterfaceAndReleaseStream(std::exchange(Selection,nullptr),IID_PPV_ARGS(&selection)):S_OK;if(Site){const auto other=CoGetInterfaceAndReleaseStream(std::exchange(Site,nullptr),IID_PPV_ARGS(&site));if(SUCCEEDED(code))code=other;}return code;}
    ~Input(){ComPtr<IShellItemArray> selection;ComPtr<IUnknown> site;Take(selection,site);}
};
struct Work:ModuleLifetime {std::shared_ptr<Input> InputValue;std::shared_ptr<Request> State;std::shared_ptr<const Summary> Expected;Action Operation=Action::Root;bool Invoke{};};
std::mutex WorkGate;std::condition_variable WorkWake;std::deque<std::unique_ptr<Work>> Pending;unsigned Workers{};std::atomic<unsigned> Reserved{};
/// <summary>固定双线程、总计16项；闲置500ms后退出，持有模块引用直到COM和封送引用均归还。</summary>
DWORD WINAPI Worker(void* raw)noexcept{
    auto module=static_cast<HMODULE>(raw);const auto initialized=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    while(true){std::unique_ptr<Work> job;{std::unique_lock lock(WorkGate);WorkWake.wait_for(lock,std::chrono::milliseconds(500),[]{return !Pending.empty();});if(Pending.empty()){--Workers;break;}job=std::move(Pending.front());Pending.pop_front();}
        ++Counters[5];HRESULT status=initialized;
        try{
            ComPtr<IShellItemArray> selection;ComPtr<IUnknown> site;status=job->InputValue->Take(selection,site);
            if(SUCCEEDED(status)&&!job->State->Canceled){auto summary=InspectSelection(selection.Get(),site.Get(),job->State->Canceled,job->Operation,job->Invoke);status=summary->Status;
                if(job->Invoke&&SUCCEEDED(status)){
                    if(job->Expected){if(job->Expected->Mount!=summary->Mount||job->Expected->Items.size()!=summary->Items.size())status=HRESULT_FROM_WIN32(ERROR_FILE_INVALID);else for(size_t i=0;i<summary->Items.size();++i){auto& before=job->Expected->Items[i];auto& after=summary->Items[i];if(_wcsicmp(before.Path.c_str(),after.Path.c_str())||before.RootRevision!=after.RootRevision||memcmp(&before.Identity,&after.Identity,sizeof(FILE_ID_INFO))){status=HRESULT_FROM_WIN32(ERROR_FILE_INVALID);break;}}}
                    if(SUCCEEDED(status)){std::vector<std::wstring> paths;const auto mask=job->Operation==Action::FreeSpace?1u:2u;for(const auto& item:summary->Items)if(job->Operation==Action::ShowSyncIssues||(item.Actions&mask))paths.push_back(item.Path);status=paths.empty()?S_FALSE:job->Operation==Action::ShowSyncIssues?QueueSyncIssuesRequest(std::move(paths)):QueuePlaceholderRequest(job->Operation==Action::KeepOnDevice,std::move(paths));if(status==S_OK)++Counters[9];}
                }
                if(!job->Invoke&&!job->State->Canceled){job->State->Value=std::move(summary);job->State->Completed=GetTickCount64();job->State->Ready.store(true,std::memory_order_release);}
            }
        }catch(...){status=E_FAIL;}
        if(job->Invoke&&FAILED(status)){
            ++Counters[10];Counters[11]=static_cast<unsigned long>(status);
#ifndef HYBRIDMOUNT_ISOLATED_TEST_PIPE
            RecordNativeFailure("ShellServer",status,"MenuSelectionRevalidation");
#endif
        }
        if(!job->Invoke&&!job->State->Ready&&!job->State->Canceled){try{auto failed=std::make_shared<Summary>();failed->Status=status;job->State->Value=std::move(failed);}catch(...){}job->State->Completed=GetTickCount64();job->State->Ready.store(true,std::memory_order_release);}
        TraceShellProbeNumber(L"MenuQueued",Counters[3].load());TraceShellProbeNumber(L"MenuInspected",Counters[8].load());TraceShellProbeNumber(L"MenuLastStatus",static_cast<unsigned long>(status));TraceShellProbeNumber(L"MenuLastCanceled",job->State->Canceled?1:0);
        job.reset();--Counters[5];++Counters[6];--Reserved;
    }
    if(SUCCEEDED(initialized))CoUninitialize();FreeLibraryAndExitThread(module,0);
}
HRESULT Queue(IShellItemArray* selection,IUnknown* site,const std::shared_ptr<Request>& request,Action operation,bool invoke,std::shared_ptr<const Summary> expected={}){
    auto count=Reserved.load();while(count<16&&!Reserved.compare_exchange_weak(count,count+1)){}if(count>=16){++Counters[4];return HRESULT_FROM_WIN32(ERROR_BUSY);}
    auto high=Counters[7].load();while(high<count+1&&!Counters[7].compare_exchange_weak(high,count+1)){};
    try{
        auto work=std::make_unique<Work>();work->State=request;request->Actions=operation==Action::FreeSpace||operation==Action::KeepOnDevice;work->Operation=operation;work->Invoke=invoke;work->Expected=std::move(expected);work->InputValue=std::make_shared<Input>();const auto code=work->InputValue->Capture(selection,site);if(FAILED(code)){--Reserved;return code;}
        std::unique_lock lock(WorkGate);
        if(Workers<2){HMODULE hold{};if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,reinterpret_cast<LPCWSTR>(&Worker),&hold)){--Reserved;return HRESULT_FROM_WIN32(GetLastError());}++Workers;auto thread=CreateThread(nullptr,0,Worker,hold,0,nullptr);if(thread)CloseHandle(thread);else{--Workers;FreeLibrary(hold);if(!Workers){--Reserved;return HRESULT_FROM_WIN32(GetLastError());}}}
        Pending.push_back(std::move(work));++Counters[3];WorkWake.notify_one();return S_OK;
    }catch(...){--Reserved;return E_OUTOFMEMORY;}
}
struct StateTiming{LARGE_INTEGER Start{};StateTiming(){QueryPerformanceCounter(&Start);++Counters[0];}~StateTiming(){LARGE_INTEGER end{},frequency{};QueryPerformanceCounter(&end);QueryPerformanceFrequency(&frequency);auto elapsed=static_cast<unsigned long long>((end.QuadPart-Start.QuadPart)*1000000/frequency.QuadPart);auto high=Counters[12].load();while(high<elapsed&&!Counters[12].compare_exchange_weak(high,elapsed)){};}};
}
struct Context::Impl{
    SRWLOCK Gate=SRWLOCK_INIT;SelectionKey KeyValue;ComPtr<IShellItemArray> Selection;ComPtr<IUnknown> Site;std::shared_ptr<Request> Current;ULONGLONG Generation{};
    ~Impl(){if(Current)Current->Canceled=true;}
};
Context::Context():_impl(std::make_unique<Impl>()){}Context::~Context()=default;
void Context::Invalidate()noexcept{auto& p=*_impl;AcquireSRWLockExclusive(&p.Gate);if(p.Current)p.Current->Canceled=true;p.Current.reset();p.Selection.Reset();p.Site.Reset();++p.Generation;ReleaseSRWLockExclusive(&p.Gate);}
HRESULT Context::State(Action action,IShellItemArray* selection,IUnknown* site,BOOL slow,EXPCMDSTATE* state)noexcept{
    StateTiming timer;if(!state)return E_POINTER;*state=ECS_HIDDEN;if(!selection&&!site)return S_OK;auto& p=*_impl;auto key=Key(selection,site);if(!key.Valid)return S_OK;
    if(!TryAcquireSRWLockExclusive(&p.Gate)){++Counters[2];return E_PENDING;}
    std::shared_ptr<Request> request;
    try{
        if(p.Current&&p.KeyValue.Same(key)){auto current=p.Current;if(current->Ready.load(std::memory_order_acquire)&&GetTickCount64()-current->Completed<250){auto value=current->Value;const auto basic=action==Action::Root||action==Action::ShowSyncIssues;const auto usable=basic?(value&&SUCCEEDED(value->Status)||!current->Actions):current->Actions;if(usable){if(value&&SUCCEEDED(value->Status)){const auto mask=action==Action::FreeSpace?1u:2u;if(basic||(value->Actions&mask))*state=ECS_ENABLED;}++Counters[1];ReleaseSRWLockExclusive(&p.Gate);return S_OK;}}if(!current->Ready){ReleaseSRWLockExclusive(&p.Gate);++Counters[2];return E_PENDING;}}
        if(!slow){ReleaseSRWLockExclusive(&p.Gate);++Counters[2];return E_PENDING;}
        request=std::make_shared<Request>();request->Generation=++p.Generation;if(p.Current)p.Current->Canceled=true;p.Current=request;p.KeyValue=std::move(key);p.Selection=selection;p.Site=site;ReleaseSRWLockExclusive(&p.Gate);
        auto status=Queue(selection,site,request,action,false);if(FAILED(status)){auto value=std::make_shared<Summary>();value->Status=status;request->Value=std::move(value);request->Completed=GetTickCount64();request->Ready.store(true,std::memory_order_release);}++Counters[2];return E_PENDING;
    }catch(...){if(!request)ReleaseSRWLockExclusive(&p.Gate);else{request->Completed=GetTickCount64();request->Ready.store(true,std::memory_order_release);}return E_OUTOFMEMORY;}
}
HRESULT Context::Invoke(Action action,IShellItemArray* selection,IUnknown* site)noexcept{
    if(action==Action::Root)return S_OK;if(!selection&&!site)return E_INVALIDARG;
    try{auto key=Key(selection,site);if(!key.Valid)return E_INVALIDARG;std::shared_ptr<const Summary> expected;auto& p=*_impl;if(!TryAcquireSRWLockShared(&p.Gate))return HRESULT_FROM_WIN32(ERROR_BUSY);{if(p.Current&&p.KeyValue.Same(key)&&p.Current->Ready.load(std::memory_order_acquire)&&p.Current->Value&&SUCCEEDED(p.Current->Value->Status))expected=p.Current->Value;ReleaseSRWLockShared(&p.Gate);}return Queue(selection,site,std::make_shared<Request>(),action,true,std::move(expected));}catch(...){return E_OUTOFMEMORY;}
}
DWORD ProbeDelay()noexcept{
#ifdef HYBRIDMOUNT_ISOLATED_TEST_PIPE
    return TestDelay.load();
#else
    return 0;
#endif
}
}
/// <summary>原生只读诊断导出；固定14项计数，不包含路径／账号，测试和按需诊断共用。</summary>
extern "C" __declspec(dllexport) HRESULT WINAPI HybridMountMenuDiagnostics(unsigned long long* values,DWORD length)noexcept{if(!values||length<14)return E_INVALIDARG;for(size_t i=0;i<14;++i)values[i]=hm::menu::Counters[i].load();values[13]=hm::menu::Reserved.load();return S_OK;}
/// <summary>仅隔离测试映像可调采样延迟；生产拒绝，避免额外运行配置。</summary>
extern "C" __declspec(dllexport) HRESULT WINAPI HybridMountMenuTestDelay(DWORD milliseconds)noexcept{
#ifdef HYBRIDMOUNT_ISOLATED_TEST_PIPE
    if(milliseconds>1000)return E_INVALIDARG;hm::menu::TestDelay=milliseconds;return S_OK;
#else
    UNREFERENCED_PARAMETER(milliseconds);return E_NOTIMPL;
#endif
}

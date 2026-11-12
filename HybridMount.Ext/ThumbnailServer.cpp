#include "Extension.h"
#include <algorithm>
#include <thread>
#include <memory>
using namespace Microsoft::WRL;
namespace {
    /// <summary>仅创建云缩略图对象；上下文随宿主存在，关闭后拒绝新的类实例与缓存回调。</summary>
    class ThumbnailFactory final:public RuntimeClass<RuntimeClassFlags<ClassicCom>,IClassFactory>,private ModuleLifetime{
        ThumbnailCacheSource _source;bool _thumbnail;std::atomic<long> _locks{0};
    public:
        explicit ThumbnailFactory(ThumbnailCacheSource source,bool thumbnail):_source(source),_thumbnail(thumbnail){}
        IFACEMETHODIMP CreateInstance(IUnknown* outer,REFIID iid,void** result) override{
            if(!result)return E_POINTER;*result=nullptr;if(outer)return CLASS_E_NOAGGREGATION;
            if(_source.Closing->load())return CO_E_SERVER_STOPPING;
            if(_thumbnail)TraceShellProbe(L"ThumbnailFactory");return _thumbnail?CreateThumbnailHandler(iid,result,&_source):CreateMenuHandler(iid,result);
        }
        IFACEMETHODIMP LockServer(BOOL lock) override{if(lock){++_locks;++g_objects;}else{auto count=_locks.load();while(count>0&&!_locks.compare_exchange_weak(count,count-1)){}if(count==0)return E_UNEXPECTED;--g_objects;}return S_OK;}
    };
}
namespace {
/// <summary>菜单STA与缩略图MTA分别拥有工厂和注册Cookie；协调线程只控制停止及活动排空。</summary>
struct ServerApartment {
    ThumbnailCacheSource Source;bool Thumbnail;HANDLE Stop,Ready{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    std::atomic<HRESULT> Status{E_PENDING};std::atomic<bool> FactoryAlive{};std::thread Worker;
    ServerApartment(ThumbnailCacheSource source,bool thumbnail,HANDLE stop):Source(source),Thumbnail(thumbnail),Stop(stop){}
    ~ServerApartment(){if(Worker.joinable())Worker.join();if(Ready)CloseHandle(Ready);}
    void Start(){Worker=std::thread([this]{Run();});}
    void Run()noexcept{
        const auto initialized=CoInitializeEx(nullptr,Thumbnail?COINIT_MULTITHREADED:COINIT_APARTMENTTHREADED);
        DWORD cookie{};ComPtr<ThumbnailFactory> factory;HRESULT status=initialized;
        try{
            if(SUCCEEDED(status)){
                factory=Make<ThumbnailFactory>(Source,Thumbnail);FactoryAlive.store(factory.Get()!=nullptr);
                status=factory?CoRegisterClassObject(Thumbnail?HybridMountThumbnailId:HybridMountCommandId,factory.Get(),CLSCTX_LOCAL_SERVER,REGCLS_MULTIPLEUSE|REGCLS_SUSPENDED,&cookie):E_OUTOFMEMORY;
            }
            Status.store(status);SetEvent(Ready);
            while(SUCCEEDED(status)&&WaitForSingleObject(Stop,0)!=WAIT_OBJECT_0){
                if(Thumbnail){WaitForSingleObject(Stop,100);continue;}
                const auto waited=MsgWaitForMultipleObjects(1,&Stop,FALSE,100,QS_ALLINPUT);
                if(waited==WAIT_FAILED){status=HRESULT_FROM_WIN32(GetLastError());break;}
                MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){
                    if(message.message==WM_QUIT){Source.Closing->store(true);continue;}
                    TranslateMessage(&message);DispatchMessageW(&message);
                }
            }
        }catch(...){status=E_OUTOFMEMORY;Status.store(status);SetEvent(Ready);}
        if(cookie)CoRevokeClassObject(cookie);
        if(factory){CoDisconnectObject(factory.Get(),0);factory.Reset();}FactoryAlive.store(false);
        Status.store(status);if(SUCCEEDED(initialized))CoUninitialize();
    }
};
}
/// <summary>原ABI的协调入口；独立STA菜单不再被MTA缩略图调用阻塞。撤销注册前等待所有对象和后台通知收尾。</summary>
extern "C" HRESULT WINAPI HybridMountRunShellServerWithActivity(HybridMountReadThumbnail read,void* context,HANDLE stopEvent,DWORD idleMilliseconds,DWORD flags,BOOL(WINAPI* busy)(void*)) noexcept{
    if(!flags||(flags&~3u)||((flags&1)&&!read)||!stopEvent||idleMilliseconds<100)return E_INVALIDARG;
    // 旧宿主允许从已初始化的STA调用；协调入口不拥有工厂接口，保留调用方公寓且仅释放本次初始化。
    const auto initialized=CoInitializeEx(nullptr,COINIT_MULTITHREADED);const auto ownsCom=SUCCEEDED(initialized);
    if(FAILED(initialized)&&initialized!=RPC_E_CHANGED_MODE)return initialized;
    const auto stop=CreateEventW(nullptr,TRUE,FALSE,nullptr);if(!stop){const auto error=HRESULT_FROM_WIN32(GetLastError());if(ownsCom)CoUninitialize();return error;}
    HRESULT status=S_OK;std::atomic<bool> closing{false};std::unique_ptr<ServerApartment> menu,thumbnail;
    try{
        const ThumbnailCacheSource source{read,context,&closing};
        if(flags&2){menu=std::make_unique<ServerApartment>(source,false,stop);if(!menu->Ready)throw std::bad_alloc();menu->Start();WaitForSingleObject(menu->Ready,INFINITE);status=menu->Status.load();}
        if(SUCCEEDED(status)&&(flags&1)){thumbnail=std::make_unique<ServerApartment>(source,true,stop);if(!thumbnail->Ready)throw std::bad_alloc();thumbnail->Start();WaitForSingleObject(thumbnail->Ready,INFINITE);status=thumbnail->Status.load();}
        if(SUCCEEDED(status))status=CoResumeClassObjects();
        const auto baseline=static_cast<long>(((flags&1)?1:0)+((flags&2)?1:0));auto lastActive=GetTickCount64();bool suspended=false;
        while(SUCCEEDED(status)){
            const bool working=busy&&busy(context);if(g_objects.load()>baseline||working)lastActive=GetTickCount64();
            if(closing.load()||WaitForSingleObject(stopEvent,0)==WAIT_OBJECT_0||GetTickCount64()-lastActive>=idleMilliseconds){
                closing.store(true);if(!suspended){status=CoSuspendClassObjects();suspended=true;}
                if(g_objects.load()==baseline&&!working)break;
            }
            if(menu&&FAILED(menu->Status.load())){status=menu->Status.load();break;}
            if(thumbnail&&FAILED(thumbnail->Status.load())){status=thumbnail->Status.load();break;}
            // 停止后也不忙循环；对象仍可在各自公寓完成Release及最后的异步缓存通知。
            Sleep(25);
        }
    }catch(...){status=E_OUTOFMEMORY;}
    closing.store(true);CoSuspendClassObjects();
    while(true){const auto factories=static_cast<long>((menu&&menu->FactoryAlive.load()?1:0)+(thumbnail&&thumbnail->FactoryAlive.load()?1:0));if(g_objects.load()<=factories&&!(busy&&busy(context)))break;Sleep(25);}
    SetEvent(stop);thumbnail.reset();menu.reset();CloseHandle(stop);if(ownsCom)CoUninitialize();return status;
}

/// <summary>保留原宿主ABI；新宿主额外提供只读工作计数以覆盖后台解码和通知生命周期。</summary>
extern "C" HRESULT WINAPI HybridMountRunShellServer(HybridMountReadThumbnail read,void* context,HANDLE stopEvent,DWORD idleMilliseconds,DWORD flags) noexcept{return HybridMountRunShellServerWithActivity(read,context,stopEvent,idleMilliseconds,flags,nullptr);}

/// <summary>保留既有单缩略图ABI供旧宿主与隔离回归使用。</summary>
extern "C" HRESULT WINAPI HybridMountRunThumbnailServer(HybridMountReadThumbnail read,void* context,HANDLE stopEvent,DWORD idleMilliseconds) noexcept{return HybridMountRunShellServer(read,context,stopEvent,idleMilliseconds,1);}

#include "Extension.h"
#include <algorithm>
using namespace Microsoft::WRL;
namespace {
    /// <summary>仅创建云缩略图对象；上下文随宿主存在，关闭后拒绝新的类实例与缓存回调。</summary>
    class ThumbnailFactory final:public RuntimeClass<RuntimeClassFlags<ClassicCom>,IClassFactory>,private ModuleLifetime{
        ThumbnailCacheSource _source;std::atomic<long> _locks{0};
    public:
        explicit ThumbnailFactory(ThumbnailCacheSource source):_source(source){}
        IFACEMETHODIMP CreateInstance(IUnknown* outer,REFIID iid,void** result) override{
            if(!result)return E_POINTER;*result=nullptr;if(outer)return CLASS_E_NOAGGREGATION;
            if(_source.Closing->load())return CO_E_SERVER_STOPPING;
            return CreateThumbnailHandler(iid,result,&_source);
        }
        IFACEMETHODIMP LockServer(BOOL lock) override{if(lock){++_locks;++g_objects;}else{auto count=_locks.load();while(count>0&&!_locks.compare_exchange_weak(count,count-1)){}if(count==0)return E_UNEXPECTED;--g_objects;}return S_OK;}
    };
}
/// <summary>STA消息循环服务Shell；只有工厂自身的基线引用时才空闲退出，不终止仍有对象／锁的宿主。</summary>
extern "C" HRESULT WINAPI HybridMountRunThumbnailServer(HybridMountReadThumbnail read,void* context,HANDLE stopEvent,DWORD idleMilliseconds) noexcept{
    if(!read||!stopEvent||idleMilliseconds<100)return E_INVALIDARG;
    auto initialized=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);if(FAILED(initialized))return initialized;
    HRESULT status=S_OK;DWORD cookie=0;std::atomic<bool> closing{false};bool suspended=false;
    try{
        auto factory=Make<ThumbnailFactory>(ThumbnailCacheSource{read,context,&closing});if(!factory){CoUninitialize();return E_OUTOFMEMORY;}
        status=CoRegisterClassObject(HybridMountThumbnailId,factory.Get(),CLSCTX_LOCAL_SERVER,REGCLS_MULTIPLEUSE|REGCLS_SUSPENDED,&cookie);
        if(SUCCEEDED(status))status=CoResumeClassObjects();
        auto lastActive=GetTickCount64();
        while(SUCCEEDED(status)&&cookie){
            const auto count=g_objects.load();if(count>1)lastActive=GetTickCount64();
            const auto signaled=WaitForSingleObject(stopEvent,0)==WAIT_OBJECT_0;
            if(signaled||GetTickCount64()-lastActive>=idleMilliseconds){
                closing.store(true);if(!suspended){status=CoSuspendClassObjects();suspended=true;}
                if(g_objects.load()==1)break;
            }
            auto result=MsgWaitForMultipleObjects(suspended?0:1,suspended?nullptr:&stopEvent,FALSE,100,QS_ALLINPUT);
            if(result==WAIT_FAILED){status=HRESULT_FROM_WIN32(GetLastError());break;}
            MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){
                if(message.message==WM_QUIT){closing.store(true);CoSuspendClassObjects();suspended=true;}
                else{TranslateMessage(&message);DispatchMessageW(&message);}
            }
        }
        closing.store(true);if(cookie){CoRevokeClassObject(cookie);cookie=0;}CoDisconnectObject(factory.Get(),0);
    }catch(...){status=E_OUTOFMEMORY;}
    if(cookie)CoRevokeClassObject(cookie);CoUninitialize();return status;
}

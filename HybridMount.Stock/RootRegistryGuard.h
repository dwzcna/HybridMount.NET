#pragma once
#include "NativeCommon.h"
namespace hm {
/// <summary>WinRT根和项目属性与C#精确Shell清理共享SID互斥量；只有实际WinRT写入才等待，缓存查询不进入此锁。</summary>
struct RootRegistryGuard {
    Handle Mutex;bool Owned{};
    explicit RootRegistryGuard(HANDLE cancel){
        auto name=L"Global\\HybridMount.NativePreview.RootRegistry."+CurrentSid();Mutex.Value=CreateMutexW(nullptr,FALSE,name.c_str());WinCheck(Mutex.Value!=nullptr);HANDLE waits[]{Mutex.Value,cancel};auto result=WaitForMultipleObjects(2,waits,FALSE,30000);Owned=result==WAIT_OBJECT_0||result==WAIT_ABANDONED_0;
        if(!Owned)throw Failure(result==WAIT_OBJECT_0+1?HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED):result==WAIT_TIMEOUT?HRESULT_FROM_WIN32(ERROR_TIMEOUT):HRESULT_FROM_WIN32(GetLastError()));
        if(WaitForSingleObject(cancel,0)==WAIT_OBJECT_0){ReleaseMutex(Mutex.Value);Owned=false;throw Failure(HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));}
    }
    ~RootRegistryGuard(){if(Owned)ReleaseMutex(Mutex.Value);}
};
}

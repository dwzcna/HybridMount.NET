#pragma once
#include "NativeWire.h"
#include <atomic>
#include <functional>
#include <condition_variable>
#include <deque>
#include <future>
#include <map>
#include <mutex>
#include <thread>

namespace hm {
/// <summary>已认证运行时的一次命令；取消只授权停止未开始的阶段，不表示已写内容回滚。</summary>
struct RuntimeCommand {
    std::string Id,Operation;Json Data;Handle Cancel{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    bool Irreversible{};
    bool Canceled()const{return WaitForSingleObject(Cancel.Value,0)==WAIT_OBJECT_0;}
    void CheckCancel()const{Require(Irreversible||!Canceled(),HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));}
};
/// <summary>只有固定256KiB块能进入数据通道；GUID为会话内请求身份，不是原生句柄。</summary>
struct RuntimeBlock {std::string Id,FetchId;long long Offset{};std::vector<BYTE> Bytes;};
/// <summary>双向私有运行时通道；控制、回调、正文及执行线程独立，回调只入有界队列。</summary>
class RuntimeHost {
    struct Impl;std::unique_ptr<Impl> _impl;
public:
    RuntimeHost(DWORD parent,const std::wstring& nonce);~RuntimeHost();
    RuntimeHost(const RuntimeHost&)=delete;RuntimeHost& operator=(const RuntimeHost&)=delete;
    std::function<Json(RuntimeCommand&)> Execute;
    std::function<void(const RuntimeBlock&)> Content;
    std::function<void()> Stopping;
    bool Send(Json value,bool waitForCapacity=false)noexcept;
    bool IsStopping()const noexcept;
    DWORD ParentProcessId()const noexcept;
    Json Checkpoint(RuntimeCommand& command,const std::string& stage,const Json& data,bool beginIrreversible=false,bool safety=false);
    /// <summary>反向有界正文帧，只用于已授权本地读租约，控制回复不携带正文。</summary>
    void SendBlock(const std::string& request,const std::string& lease,long long offset,const std::vector<BYTE>& bytes);
    void Run();
};
/// <summary>固定参数入口；没有当前父进程和私有通道握手时不能提供操作。</summary>
int RunRuntime(DWORD parent,const std::wstring& nonce)noexcept;
}

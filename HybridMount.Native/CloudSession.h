#pragma once
#include "RuntimeOperations.h"
#include <set>
namespace hm {
/// <summary>一个同步根的原生连接及回调所有者；原生键和指针不离开本进程。</summary>
class CloudSession {
    struct Impl;std::unique_ptr<Impl> _impl;
public:
    CloudSession(RuntimeHost& host,const Json& root);~CloudSession()noexcept;
    void Connect();void Stop();void BeginClear();
    const Json& Root()const;
    bool Active(const std::string& entry)const;
    Json Activity(const std::string& entry)const;
    void Transfer(const RuntimeBlock& block);
    bool OwnsFetch(const std::string& id)const;
    void Finish(const std::string& id,NTSTATUS status);
#ifdef HYBRIDMOUNT_NATIVE_TESTS
    /// <summary>仅隔离测试可注入完成及断开失败；生产二进制不包含入口。</summary>
    void HoldTestCallback();
    void ReleaseTestCallback();
    void SetTestFailures(int finish,int disconnect);
#endif
    void SetBlocked(const std::string& entry,bool blocked);
};
}

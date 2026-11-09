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
    void SetBlocked(const std::string& entry,bool blocked);
};
}

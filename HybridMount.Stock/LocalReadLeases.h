#pragma once
#include "CloudFileOperations.h"
namespace hm {
/// <summary>仅供后台解码的有界本地读租约。CFAPI句柄、身份和预算始终留在原生进程。</summary>
class LocalReadLeases {
    struct Impl;std::unique_ptr<Impl> _impl;
public:
    explicit LocalReadLeases(RuntimeHost& host);~LocalReadLeases();
    Json Execute(RuntimeCommand& command);void Stop();
};
}

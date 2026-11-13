#pragma once
#include "RuntimeHost.h"
#include "NativeFile.h"
namespace hm {
/// <summary>运行时操作集合；所有变更通过完整受保护操作，不公开任意原生函数调用。</summary>
class RuntimeOperations {
    struct Impl;std::unique_ptr<Impl> _impl;
public:
    explicit RuntimeOperations(RuntimeHost& host);~RuntimeOperations();
    Json Execute(RuntimeCommand& command);
    void Content(const RuntimeBlock& block);
    void Stop();
    void TaskMessage(const Json& frame);
};
/// <summary>64位文件状态以十进制字符串编码，避免任何JSON浮点转换。</summary>
Json FileJson(const FileState& value);
long long Number(const Json& value,const char* key);
}

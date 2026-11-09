#pragma once
#include "CloudSession.h"
namespace hm {
/// <summary>原生保护句柄只在完整操作内部使用，退出前从同一对象返回结果戳。</summary>
Json ExecuteCloudFile(RuntimeHost& host,RuntimeCommand& command,CloudSession* session,bool& handled);
void ValidateRuntimeRoot(const Json& data,bool registered=true);
}

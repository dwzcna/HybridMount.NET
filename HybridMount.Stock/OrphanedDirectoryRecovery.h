#pragma once
#include "RuntimeHost.h"
namespace hm {
/// <summary>手动确认的孤立目录专用操作；不接受正式挂载会话或任意CFAPI函数名。</summary>
Json ExecuteOrphanedDirectory(RuntimeHost& host,RuntimeCommand& command);
}

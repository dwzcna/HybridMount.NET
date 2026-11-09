#pragma once
namespace hm {
/// <summary>无凭据的原生解码工作角色；正文范围由父进程预算服务提供，失败返回unsupported。</summary>
int RunPreviewWorker() noexcept;
}

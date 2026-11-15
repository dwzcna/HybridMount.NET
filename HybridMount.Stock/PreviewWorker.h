#pragma once
#include "NativeCommon.h"
#include <objidl.h>
namespace hm {
/// <summary>无凭据的原生解码工作角色；正文范围由父进程预算服务提供，失败返回unsupported。</summary>
int RunPreviewWorker() noexcept;
/// <summary>仅比较统一格式名，不访问路径、注册表或解码器。</summary>
bool SupportsPreviewExtension(const std::wstring& extension) noexcept;
/// <summary>受限工作进程内生成256、32、64和128四帧；不执行PE，异常由调用者转换为失败响应。</summary>
Json DecodePreviewFrames(IStream* input,const std::wstring& extension);
}

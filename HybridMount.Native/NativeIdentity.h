#pragma once
#include <windows.h>
#include <appmodel.h>
#include <string>
#include <stdexcept>

namespace hm {
/// <summary>按实际进程包身份隔离运行模式；未知错误不当作非打包。</summary>
inline std::wstring DeploymentScope() {
    UINT32 length=0;
    const auto status=GetCurrentPackageFullName(&length,nullptr);
    if(status==APPMODEL_ERROR_NO_PACKAGE)return L"Unpackaged";
    if(status==ERROR_INSUFFICIENT_BUFFER||status==ERROR_SUCCESS)return L"Packaged";
    throw std::runtime_error("Package identity unavailable");
}
/// <summary>实验根与管道前缀；正式产品和另一部署模式不属于本进程。</summary>
inline std::wstring ProductScope(){return L"HybridMount.NativePreview."+DeploymentScope();}
}

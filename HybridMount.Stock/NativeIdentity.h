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
/// <summary>实际进程应用身份；只有包身份而没有应用身份时明确返回空值。</summary>
inline std::wstring ApplicationId(){
    UINT32 count{};auto status=GetCurrentApplicationUserModelId(&count,nullptr);if(status==APPMODEL_ERROR_NO_APPLICATION||status==APPMODEL_ERROR_NO_PACKAGE)return {};if(status!=ERROR_INSUFFICIENT_BUFFER||count>2048)throw std::runtime_error("Application identity unavailable");
    std::wstring result(count,L'\0');if(GetCurrentApplicationUserModelId(&count,result.data())!=ERROR_SUCCESS)throw std::runtime_error("Application identity unavailable");result.resize(count-1);return result;
}
/// <summary>实验根与管道前缀；正式产品和另一部署模式不属于本进程。</summary>
inline std::wstring ProductScope(){return L"HybridMount.NET."+DeploymentScope();}
/// <summary>仅有实际包身份时设置桌面应用策略；保留本次子进程及其后代的包环境。</summary>
inline DWORD ChildDesktopPolicy(){return PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_OVERRIDE|PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_DISABLE_PROCESS_TREE;}
inline bool HasPackageIdentity(){return DeploymentScope()==L"Packaged";}
/// <summary>在恢复隔离子进程前核验实际包全名；查询失败不能当成非打包或继续运行。</summary>
inline std::wstring ProcessPackage(HANDLE process){
    UINT32 count{};auto status=GetPackageFullName(process,&count,nullptr);if(status==APPMODEL_ERROR_NO_PACKAGE)return {};
    if(status!=ERROR_INSUFFICIENT_BUFFER||count<2||count>2048)throw std::runtime_error("Child package query failed");
    std::wstring value(count,L'\0');if(GetPackageFullName(process,&count,value.data())!=ERROR_SUCCESS)throw std::runtime_error("Child package query failed");value.resize(count-1);return value;
}
inline void VerifyChildPackage(HANDLE process){if(ProcessPackage(GetCurrentProcess())!=ProcessPackage(process))throw std::runtime_error("Child package identity mismatch");}

}

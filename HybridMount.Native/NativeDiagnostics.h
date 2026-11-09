#pragma once
#include "NativeFile.h"
#include <cstdio>
namespace hm {
/// <summary>早期宿主写有限安全证据，可附数字版本；由下次日志页导入，不写参数或异常正文。</summary>
inline void RecordNativeFailure(const char* role,HRESULT code,const char* kind="NativeFailure",const std::string& expectedBuild={},const std::string& actualBuild={})noexcept{
    try{
        Require(std::string(role)=="ShellServer"||std::string(role)=="PreviewWorker"||std::string(role)=="NativeRuntime");PWSTR value{};Check(SHGetKnownFolderPath(FOLDERID_LocalAppData,KF_FLAG_DEFAULT,nullptr,&value));std::wstring local(value);CoTaskMemFree(value);std::wstring base;
        UINT32 count{};auto status=GetCurrentPackageFamilyName(&count,nullptr);
        if(status==ERROR_INSUFFICIENT_BUFFER){Require(count>1&&count<=2048);std::wstring family(count,L'\0');Require(GetCurrentPackageFamilyName(&count,family.data())==ERROR_SUCCESS);family.resize(count-1);base=local+L"\\Packages\\"+family+L"\\LocalState";}
        else{Require(status==APPMODEL_ERROR_NO_PACKAGE);base=local+L"\\HybridMount.NET\\NativePreview\\Unpackaged";}
        auto directory=FullPath(base+L"\\Logs\\HostDiagnostics");for(size_t end=3;;){auto next=directory.find(static_cast<wchar_t>(92),end);auto path=next==std::wstring::npos?directory:directory.substr(0,next);if(!CreateDirectoryW(path.c_str(),nullptr))Require(GetLastError()==ERROR_ALREADY_EXISTS);NoLinks(path,false);if(next==std::wstring::npos)break;end=next+1;}
        WIN32_FIND_DATAW item{};auto search=FindFirstFileW((directory+L"\\*.json").c_str(),&item);if(search!=INVALID_HANDLE_VALUE){int existing=0;do{++existing;}while(existing<=256&&FindNextFileW(search,&item));FindClose(search);Require(existing<256);}
        GUID id{};Check(CoCreateGuid(&id));wchar_t raw[40]{};Require(StringFromGUID2(id,raw,40)>0);auto key=GuidN(Utf8(raw));SYSTEMTIME utc{};GetSystemTime(&utc);char timestamp[40]{};sprintf_s(timestamp,"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",utc.wYear,utc.wMonth,utc.wDay,utc.wHour,utc.wMinute,utc.wSecond,utc.wMilliseconds);
        auto evidence=Json{{"Id",key},{"Utc",timestamp},{"Role",role},{"Type",kind},{"HResult",code},{"ExitCode",nullptr}};
        if(!expectedBuild.empty()){auto safe=[](const std::string& build){return !build.empty()&&build.size()<=64&&build.find_first_not_of("0123456789.")==std::string::npos?build:std::string("<invalid>");};evidence["ExpectedBuild"]=safe(expectedBuild);evidence["ActualBuild"]=safe(actualBuild);}
        auto text=evidence.dump();auto temporary=directory+L"\\"+Wide(key)+L".tmp",destination=directory+L"\\"+Wide(key)+L".json";
        {Handle file(CreateFileW(temporary.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));WinCheck(file.Value!=INVALID_HANDLE_VALUE);DWORD done{};WinCheck(WriteFile(file.Value,text.data(),static_cast<DWORD>(text.size()),&done,nullptr));Require(done==text.size());WinCheck(FlushFileBuffers(file.Value));}WinCheck(MoveFileExW(temporary.c_str(),destination.c_str(),MOVEFILE_WRITE_THROUGH));
    }catch(...){} // 证据写入失败不得替换原宿主退出码。
}
}

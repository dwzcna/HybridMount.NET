#pragma once
#include "NativeIdentity.h"
#include "ThirdParty/json.hpp"
#include <sddl.h>
#include <shlobj.h>
#include <bcrypt.h>
#include <vector>
#include <memory>
#include <limits>
#include <utility>
#include <algorithm>

namespace hm {
using Json=nlohmann::json;
/// <summary>跨原生边界仅传播错误码；不把路径、源地址或异常正文写入日志。</summary>
struct Failure final : std::exception { HRESULT Code;const char* Stage;explicit Failure(HRESULT code,const char* stage="operation"):Code(code),Stage(stage){} };
inline void Check(HRESULT code){if(FAILED(code))throw Failure(code);}
inline void Require(bool value,HRESULT code=E_INVALIDARG){if(!value)throw Failure(code);}
inline void WinCheck(BOOL value){if(!value)throw Failure(HRESULT_FROM_WIN32(GetLastError()));}
/// <summary>普通Windows句柄所有者；不用于CFAPI保护句柄。</summary>
struct Handle {
    HANDLE Value=INVALID_HANDLE_VALUE;
    Handle()=default;explicit Handle(HANDLE value):Value(value){}
    ~Handle(){if(Value&&Value!=INVALID_HANDLE_VALUE)CloseHandle(Value);}
    Handle(const Handle&)=delete;Handle& operator=(const Handle&)=delete;
    Handle(Handle&& other)noexcept:Value(std::exchange(other.Value,INVALID_HANDLE_VALUE)){}
};
inline std::string Utf8(const std::wstring& text){
    if(text.empty())return {};auto count=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),nullptr,0,nullptr,nullptr);Require(count>0);
    std::string result(count,'\0');Require(WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),result.data(),count,nullptr,nullptr)==count);return result;
}
inline std::wstring Wide(const std::string& text){
    if(text.empty())return {};auto count=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),nullptr,0);Require(count>0);
    std::wstring result(count,L'\0');Require(MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),result.data(),count)==count);return result;
}
inline Json Parse(const std::string& text,size_t maximum=65536){
    Require(!text.empty()&&text.size()<=maximum);
    return Json::parse(text,[](int depth,Json::parse_event_t,Json&){Require(depth<=16);return true;});
}
inline std::wstring Text(const Json& value,const char* key){return Wide(value.at(key).get<std::string>());}
inline long long Integer(const Json& value,const char* key){const auto& item=value.at(key);Require(item.is_number_integer());if(item.is_number_unsigned())Require(item.get<unsigned long long>()<=LLONG_MAX);return item.get<long long>();}
inline std::string Hex(const void* bytes,size_t size){
    static constexpr char table[]="0123456789ABCDEF";std::string result(size*2,'0');const auto data=static_cast<const BYTE*>(bytes);
    for(size_t i=0;i<size;++i){result[i*2]=table[data[i]>>4];result[i*2+1]=table[data[i]&15];}return result;
}
inline std::string Sha256(const std::string& data){
    BYTE hash[32]{};Check(HRESULT_FROM_NT(BCryptHash(BCRYPT_SHA256_ALG_HANDLE,nullptr,0,reinterpret_cast<PUCHAR>(const_cast<char*>(data.data())),static_cast<ULONG>(data.size()),hash,sizeof(hash))));return Hex(hash,sizeof(hash));
}
inline std::wstring CurrentSid(){
    HANDLE raw{};WinCheck(OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&raw));Handle token(raw);DWORD count=0;
    GetTokenInformation(raw,TokenUser,nullptr,0,&count);Require(count>0&&count<=65536);std::vector<BYTE> buffer(count);WinCheck(GetTokenInformation(raw,TokenUser,buffer.data(),count,&count));
    LPWSTR sid{};WinCheck(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid,&sid));std::wstring result(sid);LocalFree(sid);return result;
}
inline std::wstring ModuleDirectory(){
    std::wstring path(32768,L'\0');auto length=GetModuleFileNameW(nullptr,path.data(),static_cast<DWORD>(path.size()));Require(length>0&&length<path.size());path.resize(length);return path.substr(0,path.find_last_of(L'\\'));
}
inline std::wstring LocalAppData(){PWSTR path{};Check(SHGetKnownFolderPath(FOLDERID_LocalAppData,KF_FLAG_DEFAULT,nullptr,&path));std::wstring result(path);CoTaskMemFree(path);return result;}
inline std::wstring PhysicalDataDirectory(){return LocalAppData()+L"\\HybridMount.NET\\"+DeploymentScope();}
inline std::string GuidN(const std::string& text){
    GUID value{};auto wide=Wide(text);if(wide.size()==32){for(auto ch:wide)Require(iswxdigit(ch)!=0);wide.insert(20,L"-");wide.insert(16,L"-");wide.insert(12,L"-");wide.insert(8,L"-");}if(wide.size()==36)wide=L"{"+wide+L"}";Check(CLSIDFromString(wide.c_str(),&value));Require(value!=GUID_NULL);
    wchar_t buffer[40]{};Require(StringFromGUID2(value,buffer,40)>0);std::string result;for(auto ch:std::wstring(buffer))if(ch!=L'{'&&ch!=L'}'&&ch!=L'-')result+=static_cast<char>(towlower(ch));return result;
}
}

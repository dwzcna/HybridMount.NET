#pragma once
#include "NativeCommon.h"
#include <wincrypt.h>
namespace hm {
/// <summary>受限字节流帧；原生解码器复用已有匿名管道协议，不用于Shell缓存读取。</summary>
inline void Transfer(HANDLE pipe,void* memory,DWORD length,bool write){
    auto bytes=static_cast<BYTE*>(memory);while(length){DWORD count{};WinCheck(write?WriteFile(pipe,bytes,length,&count,nullptr):ReadFile(pipe,bytes,length,&count,nullptr));Require(count>0,HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE));bytes+=count;length-=count;}
}
inline Json ReadFrame(HANDLE pipe){DWORD length{};Transfer(pipe,&length,4,false);Require(length>0&&length<=1024*1024);std::string bytes(length,'\0');Transfer(pipe,bytes.data(),length,false);return Parse(bytes,1024*1024);}
inline void WriteFrame(HANDLE pipe,const Json& value){auto bytes=value.dump();Require(!bytes.empty()&&bytes.size()<=1024*1024);auto length=static_cast<DWORD>(bytes.size());Transfer(pipe,&length,4,true);Transfer(pipe,bytes.data(),length,true);}
inline std::vector<BYTE> Base64(const std::string& value){DWORD size{};WinCheck(CryptStringToBinaryA(value.data(),static_cast<DWORD>(value.size()),CRYPT_STRING_BASE64,nullptr,&size,nullptr,nullptr));Require(size<=384*1024);std::vector<BYTE> bytes(size);WinCheck(CryptStringToBinaryA(value.data(),static_cast<DWORD>(value.size()),CRYPT_STRING_BASE64,bytes.data(),&size,nullptr,nullptr));bytes.resize(size);return bytes;}
inline std::string Base64(const BYTE* bytes,DWORD size){DWORD count{};WinCheck(CryptBinaryToStringA(bytes,size,CRYPT_STRING_BASE64|CRYPT_STRING_NOCRLF,nullptr,&count));std::string result(count,'\0');WinCheck(CryptBinaryToStringA(bytes,size,CRYPT_STRING_BASE64|CRYPT_STRING_NOCRLF,result.data(),&count));result.resize(count);while(!result.empty()&&result.back()=='\0')result.pop_back();return result;}
}

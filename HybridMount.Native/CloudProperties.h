#pragma once
#include "CloudFileOperations.h"
namespace hm {
/// <summary>仅查询公开存储提供程序属性，不实例化媒体处理器或读取文件正文。</summary>
Json ReadCustomProperties(const std::wstring& path);
/// <summary>合并本提供程序拥有的编号，保全其他完整向量；未知字段或来源冲突拒绝覆盖。</summary>
bool SetCustomProperty(const Json& root,const std::wstring& path,bool directory,int id,const std::wstring& text,const std::wstring& icon,HANDLE cancel);
Json ReadShellProperties(const std::wstring& path);
DWORD ProviderState(const std::wstring& path);
}

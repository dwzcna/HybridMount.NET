#pragma once
#include "ThumbnailCache.h"
namespace hm {
/// <summary>缓存优先，完整本地内容在独立原生进程中回退；冷文件不启动工作进程或水合。</summary>
std::vector<BYTE> ReadAvailableThumbnail(const std::wstring& path,UINT size,bool asynchronous=true);
/// <summary>本轮独立进程的启动与解码数值，仅供宿主诊断，不持久化路径或正文。</summary>
struct LocalThumbnailTiming {bool Started{};ULONGLONG Startup{},Decode{};};
/// <summary>仅后台队列调用的本地256像素解码；返回已验证内容戳及阶段计时，停止事件只终止本宿主拥有的解码器。</summary>
std::vector<BYTE> DecodeLocalThumbnail(const std::wstring& path,HANDLE stopping,std::string& verifiedStamp,LocalThumbnailTiming& timing,Json* frames=nullptr);
/// <summary>仅后台校验使用的短期只读重试；复用查询上下文忙时最多退让100ms，不改变COM立即返回语义。</summary>
Json ReadBackgroundThumbnailGrant(const std::wstring& path,HANDLE stopping=nullptr);
/// <summary>只读内容身份戳；可靠USN存在时不将访问元数据ChangeTime当成正文变化。</summary>
std::string LocalThumbnailStamp(const std::wstring& path);
/// <summary>固定本地工作角色；核验启用根及完整内容，结果经匿名标准输出返回。</summary>
int RunLocalThumbnailWorker(const std::wstring& path,UINT size,HANDLE acknowledged) noexcept;
/// <summary>固定匿名管道的常驻本地解码角色；无远端、凭据或CLR，停止由唯一父拥有者发起。</summary>
int RunLocalThumbnailService() noexcept;
/// <summary>停止本进程创建的本地解码器并等待空闲回收线程；重复调用幂等。</summary>
void StopLocalThumbnailDecoder() noexcept;
/// <summary>只读本地解码器启动／复用／闲置退出计数，不启动子进程。</summary>
Json LocalThumbnailDecoderStatistics();
}

#pragma once
#include "NativeFile.h"
#include <atomic>
#include <mutex>
namespace hm {
/// <summary>正文句柄所有者。关闭只拒绝新读并置取消事件；在途操作持有shared_ptr，内核完成后才归还保护引用。</summary>
class LocalReadFile {
    std::mutex Gate;bool Closed{};unsigned Active{};
    Handle Ordinary;HANDLE Protected{};bool Referenced{};
    Handle Canceled{CreateEventW(nullptr,TRUE,FALSE,nullptr)},Idle{CreateEventW(nullptr,TRUE,TRUE,nullptr)};
public:
    HANDLE Raw{};
    LocalReadFile(const std::wstring& path,bool cloud);~LocalReadFile();
    LocalReadFile(const LocalReadFile&)=delete;LocalReadFile& operator=(const LocalReadFile&)=delete;
    /// <summary>登记和归还仅持短状态锁，绝不在锁内执行文件I/O或等待。</summary>
    void Enter();void Leave()noexcept;void RequestClose()noexcept;
    bool Drain(DWORD milliseconds)noexcept;bool Broken()const noexcept;
    HANDLE CancelHandle()const noexcept{return Canceled.Value;}
};
/// <summary>仅隔离映像可注入完成延迟／短读／错误；生产不接受相应命令字段。</summary>
struct LocalReadFault {DWORD CompletionDelay{},Error{};bool Short{};};
/// <summary>每项拥有独立缓冲、OVERLAPPED、事件和句柄引用；等待可取消、有限期。取消后最多等250ms，迟结束的内核内存仍归原操作工作线程保管（全进程最多16项）。</summary>
std::vector<BYTE> ReadLocalFile(const std::shared_ptr<LocalReadFile>& file,long long offset,DWORD length,HANDLE caller,HANDLE stopping,ULONGLONG deadline,LocalReadFault fault={});
/// <summary>只读固定计数，不生成缩略图、不访问文件系统或源。</summary>
Json LocalReadStatistics();
}

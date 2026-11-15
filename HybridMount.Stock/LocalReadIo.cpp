#include "LocalReadIo.h"
#include "NativeDiagnostics.h"
#include <array>
#include <thread>
namespace hm {
namespace {
// active同时覆盖调用方已退出但内核仍未完成的项，迟到结束不会释放容量限制。
std::array<std::atomic<unsigned long long>,12> Metrics{};
void Maximum(size_t index,unsigned long long value){auto before=Metrics[index].load();while(before<value&&!Metrics[index].compare_exchange_weak(before,value)){};}
struct Operation {
    std::shared_ptr<LocalReadFile> File;std::vector<BYTE> Bytes;OVERLAPPED Io{};
    Handle IoDone{CreateEventW(nullptr,TRUE,FALSE,nullptr)},Finished{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    std::atomic<bool> Cancel{};HRESULT Code=E_PENDING;DWORD Done{};LocalReadFault Fault;
    Operation(std::shared_ptr<LocalReadFile> file,long long offset,DWORD length,LocalReadFault fault):File(std::move(file)),Bytes(length),Fault(fault){
        WinCheck(IoDone.Value&&Finished.Value);Io.Offset=static_cast<DWORD>(offset);Io.OffsetHigh=static_cast<DWORD>(static_cast<unsigned long long>(offset)>>32);
        Io.hEvent=reinterpret_cast<HANDLE>(reinterpret_cast<ULONG_PTR>(IoDone.Value)|1);
    }
    void RequestCancel()noexcept{if(!Cancel.exchange(true))++Metrics[3];}
};
/// <summary>工作线程保管所有内核引用；GetOverlappedResult始终FALSE。即使提交调用被过滤驱动延迟，调用线程仍能取消并有界返回。</summary>
DWORD WINAPI Complete(void* raw)noexcept{
    std::unique_ptr<std::shared_ptr<Operation>> context(static_cast<std::shared_ptr<Operation>*>(raw));auto op=*context;
    try{
        if(op->Cancel)op->Code=HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED);
        else{
            const auto result=ReadFile(op->File->Raw,op->Bytes.data(),static_cast<DWORD>(op->Bytes.size()),&op->Done,&op->Io);const auto error=result?ERROR_SUCCESS:GetLastError();
            if(!result&&error==ERROR_IO_PENDING){
                ++Metrics[2];while(WaitForSingleObject(op->IoDone.Value,20)==WAIT_TIMEOUT)if(op->Cancel||WaitForSingleObject(op->File->CancelHandle(),0)==WAIT_OBJECT_0)CancelIoEx(op->File->Raw,&op->Io);
                op->Code=GetOverlappedResult(op->File->Raw,&op->Io,&op->Done,FALSE)?S_OK:HRESULT_FROM_WIN32(GetLastError());
            }else op->Code=HRESULT_FROM_WIN32(error);
        }
#ifdef HYBRIDMOUNT_NATIVE_TESTS
        // 模拟驱动迟迟不报告取消完成，不将CancelIoEx返回当成完成；真实读已经结束，但所有资源刻意继续保留。
        if(op->Fault.CompletionDelay)Sleep(op->Fault.CompletionDelay);
        if(op->Fault.Short&&op->Done)--op->Done;if(op->Fault.Error)op->Code=HRESULT_FROM_WIN32(op->Fault.Error);
#endif
    }catch(...){op->Code=E_FAIL;}
    op->File->Leave();op->File.reset();--Metrics[0];++Metrics[1];SetEvent(op->Finished.Value);return 0;
}
}
LocalReadFile::LocalReadFile(const std::wstring& path,bool cloud){
    WinCheck(Canceled.Value&&Idle.Value);
    if(cloud){Check(CfOpenFileWithOplock(path.c_str(),CF_OPEN_FILE_FLAG_EXCLUSIVE,&Protected));Referenced=CfReferenceProtectedHandle(Protected)!=FALSE;if(!Referenced){CfCloseHandle(Protected);Protected=nullptr;throw Failure(HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION));}Raw=CfGetWin32HandleFromProtectedHandle(Protected);}
    else{Ordinary.Value=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_OVERLAPPED,nullptr);WinCheck(Ordinary.Value!=INVALID_HANDLE_VALUE);Raw=Ordinary.Value;}
}
LocalReadFile::~LocalReadFile(){if(Referenced)CfReleaseProtectedHandle(Protected);if(Protected)CfCloseHandle(Protected);}
void LocalReadFile::Enter(){std::lock_guard lock(Gate);Require(!Closed,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));Require(Active<1,HRESULT_FROM_WIN32(ERROR_BUSY));++Active;ResetEvent(Idle.Value);}
void LocalReadFile::Leave()noexcept{std::lock_guard lock(Gate);if(Active&&!--Active)SetEvent(Idle.Value);}
void LocalReadFile::RequestClose()noexcept{std::lock_guard lock(Gate);Closed=true;SetEvent(Canceled.Value);}
bool LocalReadFile::Drain(DWORD milliseconds)noexcept{const auto done=WaitForSingleObject(Idle.Value,milliseconds)==WAIT_OBJECT_0;if(!done)++Metrics[11];return done;}
bool LocalReadFile::Broken()const noexcept{if(!Protected)return false;if(!CfReferenceProtectedHandle(Protected))return true;CfReleaseProtectedHandle(Protected);return false;}
std::vector<BYTE> ReadLocalFile(const std::shared_ptr<LocalReadFile>& file,long long offset,DWORD length,HANDLE caller,HANDLE stopping,ULONGLONG deadline,LocalReadFault fault){
    Require(offset>=0&&length<=256*1024);Require(!caller||WaitForSingleObject(caller,0)!=WAIT_OBJECT_0,HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));if(!length)return {};
    auto active=Metrics[0].load();while(active<16&&!Metrics[0].compare_exchange_weak(active,active+1)){}Require(active<16,HRESULT_FROM_WIN32(ERROR_BUSY));Maximum(9,active+1);
    std::shared_ptr<Operation> op;bool entered=false;
    try{file->Enter();entered=true;op=std::make_shared<Operation>(file,offset,length,fault);auto context=std::make_unique<std::shared_ptr<Operation>>(op);auto thread=CreateThread(nullptr,0,Complete,context.get(),0,nullptr);WinCheck(thread!=nullptr);context.release();CloseHandle(thread);}
    catch(...){if(entered)file->Leave();--Metrics[0];throw;}
    const auto start=GetTickCount64();HRESULT canceled=S_OK;
    HANDLE waits[]{op->Finished.Value,file->CancelHandle(),caller?caller:file->CancelHandle(),stopping?stopping:file->CancelHandle()};
    while(true){
        const auto selected=WaitForMultipleObjects(4,waits,FALSE,20);if(selected==WAIT_OBJECT_0)break;
        if(selected==WAIT_FAILED){canceled=HRESULT_FROM_WIN32(GetLastError());break;}
        if(selected!=WAIT_TIMEOUT){canceled=HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED);break;}
        if(GetTickCount64()>=deadline){++Metrics[4];canceled=HRESULT_FROM_WIN32(ERROR_TIMEOUT);break;}
        if(file->Broken()){++Metrics[5];file->RequestClose();canceled=HRESULT_FROM_WIN32(ERROR_FILE_INVALID);break;}
    }
    Metrics[7]+=GetTickCount64()-start;
    if(FAILED(canceled)){
        const auto cancelStart=GetTickCount64();op->RequestCancel();if(WaitForSingleObject(op->Finished.Value,250)!=WAIT_OBJECT_0){++Metrics[6];Maximum(10,Metrics[0].load());}
        Metrics[8]+=GetTickCount64()-cancelStart;throw Failure(canceled,"local-read-canceled");
    }
    Check(op->Code);Require(op->Done==length,HRESULT_FROM_WIN32(ERROR_HANDLE_EOF));
    Require(WaitForSingleObject(file->CancelHandle(),0)!=WAIT_OBJECT_0&&(!caller||WaitForSingleObject(caller,0)!=WAIT_OBJECT_0)&&(!stopping||WaitForSingleObject(stopping,0)!=WAIT_OBJECT_0),HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));
    Require(!file->Broken(),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));if(GetTickCount64()>=deadline){++Metrics[4];throw Failure(HRESULT_FROM_WIN32(ERROR_TIMEOUT),"local-read-canceled");}return std::move(op->Bytes);
}
Json LocalReadStatistics(){static const char* names[]{"inFlight","completed","pending","cancellations","timeouts","oplockBreaks","retained","waitMs","cancelMs","maximumInFlight","maximumRetained","closePending"};Json value=Json::object();for(size_t i=0;i<Metrics.size();++i)value[names[i]]=Metrics[i].load();return value;}
}

#include "NativeCommon.h"
#include "ThumbnailCache.h"
#include "PreviewWorker.h"
#include "LocalThumbnail.h"
#include "LocalThumbnailQueue.h"
#include "PreviewObserver.h"
#include "RuntimeHost.h"
#include "NativeDiagnostics.h"
#include "NativeWire.h"
#include <shellapi.h>
#include <cstdio>

namespace {
/// <summary>由Windows启动的纯原生COM宿主；只加载同目录扩展，缓存回调无托管依赖。</summary>
int ShellServer(const std::wstring& role){
    auto module=LoadLibraryExW((hm::ModuleDirectory()+L"\\HybridMount.Ext.dll").c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);hm::Require(module!=nullptr,HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND));
    struct ModuleScope{HMODULE Value;~ModuleScope(){FreeLibrary(Value);}} owner{module};
    using Reader=HRESULT(WINAPI*)(void*,const wchar_t*,UINT,BYTE**,DWORD*);using Run=HRESULT(WINAPI*)(Reader,void*,HANDLE,DWORD,DWORD,BOOL(WINAPI*)(void*));
    Run run{};auto proc=GetProcAddress(module,"HybridMountRunShellServerWithActivity");hm::Require(proc!=nullptr);memcpy(&run,&proc,sizeof(run));
    hm::Handle stop(CreateEventW(nullptr,TRUE,FALSE,nullptr));hm::WinCheck(stop.Value!=nullptr);
    struct QueueScope{~QueueScope(){hm::StopLocalThumbnails();}} queue;
    hm::Check(run(hm::ReadThumbnail,stop.Value,stop.Value,30000,role==L"--shell-server"?3:1,hm::LocalThumbnailWorkPending));return 0;
}
}
/// <summary>原生宿主仅接受固定角色，不隐式创建挂载；异常仅作为退出码回传。</summary>
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){
    int count{};auto argv=CommandLineToArgvW(GetCommandLineW(),&count);if(!argv)return 2;
    struct Args{LPWSTR* Value;~Args(){LocalFree(Value);}} owner{argv};
    const char* role=count>=2&&wcscmp(argv[1],L"--preview-worker")==0?"PreviewWorker":count>=2&&wcscmp(argv[1],L"--runtime")==0?"NativeRuntime":"ShellServer";
    try{
        if(count==4&&wcscmp(argv[1],L"--runtime")==0){wchar_t* end{};auto parent=wcstoul(argv[2],&end,10);hm::Require(end&&!*end&&parent>0);return hm::RunRuntime(parent,argv[3]);}
        if(count==2&&wcscmp(argv[1],L"--preview-worker")==0)return hm::RunPreviewWorker();
        if(count>=2&&count<=130&&wcscmp(argv[1],L"--preview-observer")==0){std::vector<std::string> mounts;for(int i=2;i<count;++i)mounts.push_back(hm::GuidN(hm::Utf8(argv[i])));return hm::RunPreviewObserver(mounts);}
        if(count==5&&wcscmp(argv[1],L"--local-thumbnail-worker")==0){wchar_t* end{};auto size=wcstoul(argv[3],&end,10);hm::Require(end&&!*end&&size>=1&&size<=256);auto acknowledged=wcstoull(argv[4],&end,10);hm::Require(end&&!*end&&acknowledged>0);return hm::RunLocalThumbnailWorker(argv[2],static_cast<UINT>(size),reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(acknowledged)));}
        if(count==2&&wcscmp(argv[1],L"--identity")==0){auto value=hm::Utf8(hm::ProductScope())+"\n";DWORD written{};WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),value.data(),static_cast<DWORD>(value.size()),&written,nullptr);return 0;}
#ifdef HYBRIDMOUNT_NATIVE_TESTS
        // 仅隔离映像提供批量压力入口；所有路径仍逐项经过生产根授权和完整性检查。
        if(count==2&&wcscmp(argv[1],L"--test-thumbnail-batch")==0){
            struct QueueScope{~QueueScope(){hm::StopLocalThumbnails();}} queue;auto input=hm::ReadFrame(GetStdHandle(STD_INPUT_HANDLE));auto paths=input.at("paths");hm::Require(paths.is_array()&&paths.size()<=1000);
            for(auto& path:paths){BYTE* bytes{};DWORD length{};hm::ReadThumbnail(nullptr,hm::Wide(path.get<std::string>()).c_str(),64,&bytes,&length);CoTaskMemFree(bytes);}
            auto output=hm::LocalThumbnailStatistics().dump()+"\n";DWORD written{};hm::WinCheck(WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),output.data(),static_cast<DWORD>(output.size()),&written,nullptr));return 0;
        }
#endif
        // 显式隔离性能探针复用生产异步读取；只有该角色等待采样，COM调用自身从不等待。
        if(count==5&&wcscmp(argv[1],L"--diagnose-async-thumbnail")==0){
            const auto size=wcstoul(argv[3],nullptr,10),samples=wcstoul(argv[4],nullptr,10);hm::Require(size>=1&&size<=256&&samples>=1&&samples<=1000);
            struct QueueScope{~QueueScope(){hm::StopLocalThumbnails();}} queue;hm::Json values=hm::Json::array();
            for(unsigned i=0;i<samples;++i){auto started=std::chrono::steady_clock::now();BYTE* bytes{};DWORD length{};auto code=hm::ReadThumbnail(nullptr,argv[2],size,&bytes,&length);CoTaskMemFree(bytes);auto elapsed=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-started).count();values.push_back({{"ms",elapsed},{"hit",SUCCEEDED(code)&&length>0},{"code",code}});Sleep(10);}
            auto output=hm::Json({{"samples",values},{"statistics",hm::LocalThumbnailStatistics()},{"query",hm::ThumbnailQueryStatistics()}}).dump()+"\n";DWORD written{};hm::WinCheck(WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),output.data(),static_cast<DWORD>(output.size()),&written,nullptr));return 0;
        }
        // 只读诊断复用生产缓存读取；不接受数据库路径，不写文件或启动提供程序。
        if(count==4&&(wcscmp(argv[1],L"--diagnose-thumbnail")==0||wcscmp(argv[1],L"--diagnose-available-thumbnail")==0)){
            wchar_t* end{};auto requested=wcstoul(argv[3],&end,10);hm::Require(end&&!*end&&requested>0&&requested<=256);
            std::vector<BYTE> bytes;HRESULT code=S_OK;std::string failure;try{bytes=wcscmp(argv[1],L"--diagnose-thumbnail")==0?hm::ReadCachedThumbnail(argv[2],static_cast<UINT>(requested)):hm::ReadAvailableThumbnail(argv[2],static_cast<UINT>(requested),false);}catch(const hm::Failure& error){code=error.Code;failure=error.Stage;}catch(...){code=E_FAIL;}
            hm::Json result={{"stage",hm::ThumbnailReadStage},{"failureStage",failure},{"evidence",hm::ThumbnailEvidence},{"code",code},{"hit",!bytes.empty()},{"bytes",bytes.size()},{"sha256",bytes.empty()?"":hm::Sha256(std::string(bytes.begin(),bytes.end()))}};
            auto text=result.dump()+"\n";DWORD written{};hm::WinCheck(WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),text.data(),static_cast<DWORD>(text.size()),&written,nullptr));return 0;
        }
        if(count>=2&&(wcscmp(argv[1],L"--shell-server")==0||wcscmp(argv[1],L"--thumbnail-server")==0)&&
           (count==2||(count==3&&(_wcsicmp(argv[2],L"-Embedding")==0||_wcsicmp(argv[2],L"/Embedding")==0))))return ShellServer(argv[1]);
        return 2;
    }catch(const hm::Failure& error){hm::RecordNativeFailure(role,error.Code);return static_cast<int>(error.Code);}catch(...){hm::RecordNativeFailure(role,E_FAIL);return 1;}
}

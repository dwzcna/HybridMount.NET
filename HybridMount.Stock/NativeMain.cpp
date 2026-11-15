#include "NativeCommon.h"
#include "NativeFile.h"
#include "ThumbnailCache.h"
#include "PreviewWorker.h"
#include "LocalThumbnail.h"
#include "LocalThumbnailQueue.h"
#include "RemoteThumbnailHints.h"
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
    struct CacheScope{~CacheScope(){hm::StopRemoteThumbnailHints();hm::StopLocalThumbnails();hm::StopLocalThumbnailDecoder();hm::StopThumbnailReadCache();}} cache;
    int count{};auto argv=CommandLineToArgvW(GetCommandLineW(),&count);if(!argv)return 2;
    struct Args{LPWSTR* Value;~Args(){LocalFree(Value);}} owner{argv};
    const char* role=count>=2&&wcscmp(argv[1],L"--preview-worker")==0?"PreviewWorker":count>=2&&wcscmp(argv[1],L"--runtime")==0?"NativeRuntime":"ShellServer";
    try{
        if(count==4&&wcscmp(argv[1],L"--runtime")==0){wchar_t* end{};auto parent=wcstoul(argv[2],&end,10);hm::Require(end&&!*end&&parent>0);return hm::RunRuntime(parent,argv[3]);}
        if(count==2&&wcscmp(argv[1],L"--local-thumbnail-service")==0)return hm::RunLocalThumbnailService();
        if(count==2&&wcscmp(argv[1],L"--preview-worker")==0)return hm::RunPreviewWorker();
        if(count>=4&&count<=258&&(count-2)%2==0&&(wcscmp(argv[1],L"--preview-observer")==0||wcscmp(argv[1],L"--thumbnail-focus")==0)){auto roots=hm::Json::array();for(int i=2;i<count;i+=2)roots.push_back({{"mountId",hm::GuidN(hm::Utf8(argv[i]))},{"rootPath",hm::Utf8(hm::FullPath(argv[i+1]))}});return hm::RunPreviewObserver(roots,wcscmp(argv[1],L"--thumbnail-focus")==0);}
        if(count==5&&wcscmp(argv[1],L"--local-thumbnail-worker")==0){wchar_t* end{};auto size=wcstoul(argv[3],&end,10);hm::Require(end&&!*end&&size>=1&&size<=256);auto acknowledged=wcstoull(argv[4],&end,10);hm::Require(end&&!*end&&acknowledged>0);return hm::RunLocalThumbnailWorker(argv[2],static_cast<UINT>(size),reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(acknowledged)));}
        if(count==2&&wcscmp(argv[1],L"--identity")==0){auto value=hm::Utf8(hm::ProductScope())+"\n";DWORD written{};WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),value.data(),static_cast<DWORD>(value.size()),&written,nullptr);return 0;}
#ifdef HYBRIDMOUNT_NATIVE_TESTS
        // 隔离状态机只接收内存证据，不安装事件钩子或创建真实根。
        if(count==2&&wcscmp(argv[1],L"--test-foreground-observer")==0){hm::WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),hm::TestForegroundObserver(hm::ReadFrame(GetStdHandle(STD_INPUT_HANDLE))));return 0;}
        // 隔离测试复用同一解码拥有者，支持观察真实30秒闲置退出，不启动正式应用。
        if(count==2&&wcscmp(argv[1],L"--test-local-thumbnail-reader")==0){
            while(true){auto request=hm::ReadFrame(GetStdHandle(STD_INPUT_HANDLE));if(request.at("kind")=="stop")return 0;
                hm::Json response;try{if(request.at("kind")=="decode"){std::string stamp;hm::LocalThumbnailTiming timing;hm::Json frames;auto png=hm::DecodeLocalThumbnail(hm::Text(request,"path"),nullptr,stamp,timing,&frames);response={{"hit",!png.empty()},{"frames",frames},{"started",timing.Started},{"stamp",stamp}};}response["decoder"]=hm::LocalThumbnailDecoderStatistics();}
                catch(const hm::Failure& error){response={{"hit",false},{"hresult",error.Code},{"stage",error.Stage}};}
                hm::WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),response);
            }
        }
        // 仅隔离映像验证查询复用和空闲句柄收尾；路径仍经过生产授权，不生成、不访问远端。
        if(count==2&&wcscmp(argv[1],L"--test-thumbnail-reader")==0){
            auto request=hm::ReadFrame(GetStdHandle(STD_INPUT_HANDLE));auto path=hm::Text(request,"path");const auto samples=hm::Integer(request,"samples");hm::Require(samples>=1&&samples<=2000);hm::Json results=hm::Json::array();
            for(long long i=0;i<samples;++i){auto started=std::chrono::steady_clock::now();bool hit=false;HRESULT code=S_OK;try{hit=!hm::ReadCachedThumbnail(path,64).empty();}catch(const hm::Failure& error){code=error.Code;}catch(...){code=E_FAIL;}
                results.push_back({{"hit",hit},{"code",code},{"ms",std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-started).count()}});}
            hm::WriteFrame(GetStdHandle(STD_OUTPUT_HANDLE),{{"samples",results},{"query",hm::ThumbnailQueryStatistics()}});hm::ReadFrame(GetStdHandle(STD_INPUT_HANDLE));return 0;
        }
        // 仅隔离映像提供批量压力入口；所有路径仍逐项经过生产根授权和完整性检查。
        if(count==2&&wcscmp(argv[1],L"--test-thumbnail-batch")==0){
            struct QueueScope{~QueueScope(){hm::StopLocalThumbnails();}} queue;auto input=hm::ReadFrame(GetStdHandle(STD_INPUT_HANDLE));auto paths=input.at("paths");hm::Require(paths.is_array()&&paths.size()<=1000);
            for(auto& path:paths){BYTE* bytes{};DWORD length{};hm::ReadThumbnail(nullptr,hm::Wide(path.get<std::string>()).c_str(),64,&bytes,&length);CoTaskMemFree(bytes);}
            auto output=hm::LocalThumbnailStatistics().dump()+"\n";DWORD written{};hm::WinCheck(WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),output.data(),static_cast<DWORD>(output.size()),&written,nullptr));return 0;
        }
#endif
        // 显式隔离性能探针复用生产异步读取；只有该角色等待采样，COM调用自身从不等待。
        if(count==5&&wcscmp(argv[1],L"--diagnose-async-thumbnail")==0){
            const auto size=wcstoul(argv[3],nullptr,10),samples=wcstoul(argv[4],nullptr,10);hm::Require(size>=1&&size<=256&&samples>=1&&samples<=2000);
            struct QueueScope{~QueueScope(){hm::StopLocalThumbnails();}} queue;hm::Json values=hm::Json::array();
            for(unsigned i=0;i<samples;++i){auto started=std::chrono::steady_clock::now();BYTE* bytes{};DWORD length{};auto code=hm::ReadThumbnail(nullptr,argv[2],size,&bytes,&length);CoTaskMemFree(bytes);auto elapsed=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-started).count();values.push_back({{"ms",elapsed},{"hit",SUCCEEDED(code)&&length>0},{"code",code}});Sleep(10);}
            auto output=hm::Json({{"samples",values},{"statistics",hm::LocalThumbnailStatistics()},{"query",hm::ThumbnailQueryStatistics()},{"remote",hm::RemoteThumbnailHintStatistics()}}).dump()+"\n";DWORD written{};hm::WinCheck(WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),output.data(),static_cast<DWORD>(output.size()),&written,nullptr));return 0;
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

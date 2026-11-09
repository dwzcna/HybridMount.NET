#include "NativeCommon.h"
#include "ThumbnailCache.h"
#include "PreviewWorker.h"
#include "LocalThumbnail.h"
#include "PreviewObserver.h"
#include "RuntimeHost.h"
#include "NativeDiagnostics.h"
#include <shellapi.h>
#include <cstdio>

namespace {
/// <summary>由Windows启动的纯原生COM宿主；只加载同目录扩展，缓存回调无托管依赖。</summary>
int ShellServer(const std::wstring& role){
    auto module=LoadLibraryExW((hm::ModuleDirectory()+L"\\HybridMount.Ext.dll").c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);hm::Require(module!=nullptr,HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND));
    struct ModuleScope{HMODULE Value;~ModuleScope(){FreeLibrary(Value);}} owner{module};
    using Reader=HRESULT(WINAPI*)(void*,const wchar_t*,UINT,BYTE**,DWORD*);using Run=HRESULT(WINAPI*)(Reader,void*,HANDLE,DWORD,DWORD);
    Run run{};auto proc=GetProcAddress(module,"HybridMountRunShellServer");hm::Require(proc!=nullptr);memcpy(&run,&proc,sizeof(run));
    hm::Handle stop(CreateEventW(nullptr,TRUE,FALSE,nullptr));hm::WinCheck(stop.Value!=nullptr);
    hm::Check(run(hm::ReadThumbnail,nullptr,stop.Value,30000,role==L"--shell-server"?3:1));return 0;
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
        // 只读诊断复用生产缓存读取；不接受数据库路径，不写文件或启动提供程序。
        if(count==4&&(wcscmp(argv[1],L"--diagnose-thumbnail")==0||wcscmp(argv[1],L"--diagnose-available-thumbnail")==0)){
            wchar_t* end{};auto requested=wcstoul(argv[3],&end,10);hm::Require(end&&!*end&&requested>0&&requested<=256);
            std::vector<BYTE> bytes;HRESULT code=S_OK;std::string failure;try{bytes=wcscmp(argv[1],L"--diagnose-thumbnail")==0?hm::ReadCachedThumbnail(argv[2],static_cast<UINT>(requested)):hm::ReadAvailableThumbnail(argv[2],static_cast<UINT>(requested));}catch(const hm::Failure& error){code=error.Code;failure=error.Stage;}catch(...){code=E_FAIL;}
            hm::Json result={{"stage",hm::ThumbnailReadStage},{"failureStage",failure},{"evidence",hm::ThumbnailEvidence},{"code",code},{"hit",!bytes.empty()},{"bytes",bytes.size()},{"sha256",bytes.empty()?"":hm::Sha256(std::string(bytes.begin(),bytes.end()))}};
            auto text=result.dump()+"\n";DWORD written{};hm::WinCheck(WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),text.data(),static_cast<DWORD>(text.size()),&written,nullptr));return 0;
        }
        if(count>=2&&(wcscmp(argv[1],L"--shell-server")==0||wcscmp(argv[1],L"--thumbnail-server")==0)&&
           (count==2||(count==3&&(_wcsicmp(argv[2],L"-Embedding")==0||_wcsicmp(argv[2],L"/Embedding")==0))))return ShellServer(argv[1]);
        return 2;
    }catch(const hm::Failure& error){hm::RecordNativeFailure(role,error.Code);return static_cast<int>(error.Code);}catch(...){hm::RecordNativeFailure(role,E_FAIL);return 1;}
}

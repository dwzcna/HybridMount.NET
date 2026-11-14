#include "NativeLanguage.h"
#include "NativeIdentity.h"
#include <sddl.h>
#include <bcrypt.h>
#include <array>
#include <atomic>
#include <memory>
#include <vector>
#include <cwctype>
#include "NativeLanguageFallback.h"
namespace hm::language { namespace {
struct Mapping {
    HANDLE Handle{}; const volatile ULONG* View{};
    ~Mapping(){if(View)UnmapViewOfFile(const_cast<const ULONG*>(View));if(Handle)CloseHandle(Handle);}
};
std::atomic<std::shared_ptr<Mapping>> Shared;
std::atomic<unsigned> Language{0x0804};
std::atomic<ULONGLONG> NextRefresh{0};std::atomic<bool> Working{false},Ready{false};
wchar_t Strings[3][8][512]{};
unsigned Index(unsigned lcid)noexcept{return lcid==0x0409?1:lcid==0x0c04?2:0;}
bool Valid(unsigned value)noexcept{return value==0x0804||value==0x0409||value==0x0c04;}
/// <summary>后台读取Windows真实优先列表，不受应用语言覆盖影响。</summary>
unsigned SystemLanguage(){
    ULONG count{},size{};if(!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME,&count,nullptr,&size)||!size||size>32768)return 0x0804;
    std::vector<wchar_t> data(size);if(!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME,&count,data.data(),&size))return 0x0804;
    for(const wchar_t* item=data.data();*item;item+=wcslen(item)+1){std::wstring value(item);for(auto& c:value)c=static_cast<wchar_t>(towlower(c));
        if(value==L"en"||value.rfind(L"en-",0)==0)return 0x0409;
        if(value==L"zh"||value.rfind(L"zh-",0)==0)return value.find(L"hant")!=value.npos||value.find(L"-hk")!=value.npos||value.find(L"-tw")!=value.npos||value.find(L"-mo")!=value.npos||value.find(L"-cht")!=value.npos?0x0c04:0x0804;
    }return 0x0804;
}
/// <summary>SID／Session／包Family的命名与托管桥一致；只在后台建立映射。</summary>
std::wstring MapName(){
    HANDLE token{};if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))return {};
    DWORD bytes{};GetTokenInformation(token,TokenUser,nullptr,0,&bytes);std::vector<BYTE> buffer(bytes);
    const auto ok=GetTokenInformation(token,TokenUser,buffer.data(),bytes,&bytes);CloseHandle(token);if(!ok)return {};
    LPWSTR sid{};if(!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid,&sid))return {};
    std::wstring identity(sid);LocalFree(sid);DWORD session{};if(!ProcessIdToSessionId(GetCurrentProcessId(),&session))return {};
    UINT32 count{};const auto code=GetCurrentPackageFamilyName(&count,nullptr);std::wstring family=L"Unpackaged";
    if(code!=APPMODEL_ERROR_NO_PACKAGE){if(code!=ERROR_INSUFFICIENT_BUFFER||count>2048)return {};family.resize(count);if(GetCurrentPackageFamilyName(&count,family.data())!=ERROR_SUCCESS)return {};family.resize(count-1);}
    const auto utf8Count=WideCharToMultiByte(CP_UTF8,0,family.c_str(),static_cast<int>(family.size()),nullptr,0,nullptr,nullptr);if(utf8Count<=0)return {};
    std::string utf8(utf8Count,'\0');WideCharToMultiByte(CP_UTF8,0,family.c_str(),static_cast<int>(family.size()),utf8.data(),utf8Count,nullptr,nullptr);
    BCRYPT_ALG_HANDLE algorithm{};if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return {};
    BYTE hash[32]{};const auto status=BCryptHash(algorithm,nullptr,0,reinterpret_cast<PUCHAR>(utf8.data()),static_cast<ULONG>(utf8.size()),hash,sizeof(hash));BCryptCloseAlgorithmProvider(algorithm,0);if(status<0)return {};
    std::wstring hex;constexpr wchar_t digits[]=L"0123456789ABCDEF";for(auto b:hash){hex+=digits[b>>4];hex+=digits[b&15];}
    return L"Local\\HybridMount.UiLanguage.v1."+std::to_wstring(session)+L"."+identity+L"."+hex;
}
/// <summary>双读提交代次，拒绝部分写入或未知版本；system模式由Windows缓存决定。</summary>
bool Read(const std::shared_ptr<Mapping>& map,unsigned& value,bool& system)noexcept{
    if(!map||!map->View)return false;const auto p=map->View;const auto before=p[5];MemoryBarrier();if(before&1)return false;
    const auto magic=p[0],version=p[1],lcid=p[2],mode=p[3];MemoryBarrier();if(before!=p[5]||magic!=0x484d4c47||version!=1||!Valid(lcid)||mode>1)return false;
    value=lcid;system=mode!=0;return true;
}
/// <summary>专用加载线程临时使用Win32 MUI优先列表；资源模块为标准LN，最终回退由其元数据指定。</summary>
void LoadStrings(HMODULE module){
    wchar_t path[32768]{};const auto length=GetModuleFileNameW(module,path,ARRAYSIZE(path));if(!length||length>=ARRAYSIZE(path))return;
    std::wstring directory(path,length);directory.resize(directory.find_last_of(L"\\/")+1);
    struct ThreadLanguages {
        std::vector<wchar_t> Previous;
        ThreadLanguages(){ULONG count{},size{};if(GetThreadPreferredUILanguages(MUI_LANGUAGE_NAME,&count,nullptr,&size)&&size&&size<=32768){Previous.resize(size);if(!GetThreadPreferredUILanguages(MUI_LANGUAGE_NAME,&count,Previous.data(),&size))Previous.clear();}}
        ~ThreadLanguages(){ULONG count{};SetThreadPreferredUILanguages(MUI_LANGUAGE_NAME,Previous.empty()?nullptr:Previous.data(),&count);}
    } restore;
    struct DataModule { HMODULE Handle{}; ~DataModule(){if(Handle)FreeLibrary(Handle);} } resources{LoadLibraryExW((directory+L"HybridMount.Ext.resources.dll").c_str(),nullptr,LOAD_LIBRARY_AS_DATAFILE|LOAD_LIBRARY_AS_IMAGE_RESOURCE)};
    const wchar_t* names[]={L"zh-CN",L"en-US",L"zh-HK"};
    for(unsigned language=0;language<3;language++){
        std::wstring preferred=names[language];preferred.push_back(L'\0');if(language){preferred+=L"zh-CN";preferred.push_back(L'\0');}preferred.push_back(L'\0');
        ULONG count{};const auto configured=SetThreadPreferredUILanguages(MUI_LANGUAGE_NAME,preferred.c_str(),&count)&&count;
        for(unsigned text=0;text<8;text++)if(!resources.Handle||!configured||!LoadStringW(resources.Handle,1001+text,Strings[language][text],512))wcscpy_s(Strings[language][text],Fallback[language][text]);
    }
    Ready.store(true,std::memory_order_release);
}
/// <summary>短后台任务保有模块引用，收尾立即释放；不存在常驻线程或COM卸载死锁。</summary>
DWORD WINAPI Refresh(void* parameter){
    const auto module=static_cast<HMODULE>(parameter);
    try{
        if(!Ready.load(std::memory_order_acquire))LoadStrings(module);
        auto map=Shared.load();if(!map){const auto name=MapName();if(!name.empty()){auto next=std::make_shared<Mapping>();next->Handle=OpenFileMappingW(FILE_MAP_READ,FALSE,name.c_str());if(next->Handle){next->View=static_cast<const volatile ULONG*>(MapViewOfFile(next->Handle,FILE_MAP_READ,0,0,64));if(next->View){Shared.store(next);map=next;}}}}
        unsigned language{};bool system{};
        if(!Read(map,language,system)||system){wchar_t choice[64]{};DWORD bytes=sizeof(choice);const auto path=L"Software\\HybridMount.NET\\"+hm::DeploymentScope();
            if(RegGetValueW(HKEY_CURRENT_USER,path.c_str(),L"UiLanguage",RRF_RT_REG_SZ,nullptr,choice,&bytes)==ERROR_SUCCESS&&_wcsicmp(choice,L"en-US")==0)language=0x0409;
            else if(_wcsicmp(choice,L"zh-HK")==0)language=0x0c04;else if(_wcsicmp(choice,L"zh-CN")==0)language=0x0804;else language=SystemLanguage();
        }Language.store(language,std::memory_order_release);
    }catch(...){/* 仅展示缓存失败，保留上次有效语言。 */}
    NextRefresh.store(GetTickCount64()+5000);Working.store(false);
#ifdef HYBRIDMOUNT_SHELL_EXTENSION
    FreeLibraryAndExitThread(module,0);
#else
    return 0;
#endif
}
void Request()noexcept{
    if(GetTickCount64()<NextRefresh.load()||Working.exchange(true))return;
    HMODULE module{};
#ifdef HYBRIDMOUNT_SHELL_EXTENSION
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,reinterpret_cast<LPCWSTR>(&Request),&module)){Working.store(false);return;}
#else
    module=GetModuleHandleW(nullptr);
#endif
    if(auto thread=CreateThread(nullptr,0,Refresh,module,0,nullptr))CloseHandle(thread);else{
#ifdef HYBRIDMOUNT_SHELL_EXTENSION
        FreeLibrary(module);
#endif
        NextRefresh.store(GetTickCount64()+5000);Working.store(false);
    }
}
}
unsigned Current()noexcept{
    unsigned value{};bool system{};if(Read(Shared.load(),value,system)&&!system)Language.store(value,std::memory_order_release);Request();return Language.load(std::memory_order_acquire);
}
const wchar_t* Get(Text text)noexcept{
    const auto language=Index(Current()),index=static_cast<unsigned>(text);if(index>=8)return L"";
    return Ready.load(std::memory_order_acquire)?Strings[language][index]:Fallback[language][index];
}
}

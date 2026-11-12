#include "VisiblePreviewItems.h"
#include "NativeFile.h"
#include <UIAutomation.h>
#include <wrl.h>
#include <set>
#include <future>
#include <condition_variable>
#include <mutex>
#include <optional>
#include <map>
#include <atomic>
using Microsoft::WRL::ComPtr;
namespace hm {
namespace {
std::atomic<ULONGLONG> SamplingSince{};
bool MediaName(const std::wstring& name){auto dot=name.find_last_of(L'.');if(dot==std::wstring::npos)return false;auto extension=name.substr(dot);for(auto& value:extension)value=static_cast<wchar_t>(towlower(value));for(auto item:{L".jpg",L".jpeg",L".png",L".bmp",L".gif",L".tif",L".tiff",L".heic",L".heif",L".webp",L".avif",L".mp4",L".exe"})if(extension==item)return true;return false;}
/// <summary>名称只作为根内单层本地提示；隐藏扩展名时最多检查八个同名候选，歧义不猜测。</summary>
std::wstring ResolveVisibleName(const std::wstring& directory,const std::wstring& name){
    if(name.empty()||name.size()>260||name==L"."||name==L".."||name.find_first_of(L"\\/:*?\"<>|")!=std::wstring::npos)return {};
    auto find=[&](const std::wstring& pattern){std::wstring selected;WIN32_FIND_DATAW data{};auto search=FindFirstFileExW((directory+L"\\"+pattern).c_str(),FindExInfoBasic,&data,FindExSearchNameMatch,nullptr,0);if(search==INVALID_HANDLE_VALUE)return selected;
        struct Search{HANDLE Value;~Search(){FindClose(Value);}} owner{search};int candidates{};do{if(++candidates>8)return std::wstring{};if((data.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)||!MediaName(data.cFileName))continue;if(!selected.empty())return std::wstring{};selected=data.cFileName;}while(FindNextFileW(search,&data));return selected;};
    auto exact=find(name);if(!exact.empty())return FullPath(directory+L"\\"+exact);if(MediaName(name))return {};auto extension=find(name+L".*");return extension.empty()?std::wstring{}:FullPath(directory+L"\\"+extension);
}

/// <summary>UIA客户端只属于专用MTA线程；至多一项请求，调用方250ms到期后丢弃迟到结果。</summary>
class ViewSampler {
    struct Request {HWND Window{};RECT Viewport{};std::wstring Directory;ULONGLONG Deadline{};std::shared_ptr<PreviewViewGuard> Guard;std::promise<PreviewViewItems> Result;};
    std::mutex Gate;std::condition_variable Changed;std::shared_ptr<Request> Pending;bool Closed{},Active{};std::thread Worker;
    PreviewViewItems Sample(IUIAutomation2* automation,Request& request){
        PreviewViewItems result;const auto started=GetTickCount64(),deadline=std::min(request.Deadline,started+200);
        try{
            ComPtr<IUIAutomationCacheRequest> cache;Check(automation->CreateCacheRequest(&cache));Check(cache->put_TreeScope(TreeScope_Element));
            for(auto property:{UIA_NamePropertyId,UIA_ControlTypePropertyId,UIA_IsOffscreenPropertyId,UIA_BoundingRectanglePropertyId})Check(cache->AddProperty(property));
            ComPtr<IUIAutomationTreeWalker> walker;Check(automation->get_ControlViewWalker(&walker));std::set<std::wstring> seen,names;std::vector<RECT> rectangles;
            const auto viewport=request.Viewport;const auto width=viewport.right-viewport.left,height=viewport.bottom-viewport.top;Require(width>0&&height>0);
            for(int row=0;row<8&&result.Paths.size()<64;++row)for(int column=0;column<16&&result.Paths.size()<64;++column){
                if(GetTickCount64()>=deadline){result.Error=HRESULT_FROM_WIN32(ERROR_TIMEOUT);result.Milliseconds=GetTickCount64()-started;return result;}POINT point{viewport.left+(column*2+1)*width/32,viewport.top+(row*2+1)*height/16};
                if(std::any_of(rectangles.begin(),rectangles.end(),[&](const RECT& rectangle){return PtInRect(&rectangle,point)!=FALSE;}))continue;
                ++result.Candidates;Require(!request.Guard||request.Guard->Current(),HRESULT_FROM_WIN32(ERROR_CANCELLED));Require(IsWindow(request.Window)&&IsWindowVisible(request.Window),HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE));
                // 覆盖在视图上方的其他窗口不能提供候选；只读命中测试不改变焦点或滚动。
                if(GetAncestor(WindowFromPoint(point),GA_ROOT)!=GetAncestor(request.Window,GA_ROOT))continue;
                ComPtr<IUIAutomationElement> item;Check(automation->ElementFromPointBuildCache(point,cache.Get(),&item));
                for(int depth=0;item&&depth<6;++depth){
                    Require(GetTickCount64()<deadline,HRESULT_FROM_WIN32(ERROR_TIMEOUT));Require(!request.Guard||request.Guard->Current(),HRESULT_FROM_WIN32(ERROR_CANCELLED));CONTROLTYPEID type{};Check(item->get_CachedControlType(&type));
                    if(type==UIA_ListItemControlTypeId||type==UIA_DataItemControlTypeId){
                        RECT rectangle{},intersection{};BOOL offScreen{};Check(item->get_CachedIsOffscreen(&offScreen));Check(item->get_CachedBoundingRectangle(&rectangle));
                        if(!offScreen&&IntersectRect(&intersection,&viewport,&rectangle)&&PtInRect(&rectangle,point)){
                            rectangles.push_back(rectangle);BSTR raw{};Check(item->get_CachedName(&raw));std::wstring name(raw?raw:L"",raw?SysStringLen(raw):0);SysFreeString(raw);
                            Require(!request.Guard||request.Guard->Current(),HRESULT_FROM_WIN32(ERROR_CANCELLED));if(!names.insert(name).second)break;
                            auto path=ResolveVisibleName(request.Directory,name);if(!path.empty()&&path.size()<=4096&&seen.insert(path).second)result.Paths.push_back(Utf8(path));
                        }
                        break;
                    }
                    // 列表空白及视图容器不是媒体项，不继续回溯至整个Explorer框架。
                    if(type==UIA_ListControlTypeId||type==UIA_DataGridControlTypeId||type==UIA_WindowControlTypeId||(type==UIA_PaneControlTypeId&&depth>0))break;
                    ComPtr<IUIAutomationElement> parent;Check(walker->GetParentElementBuildCache(item.Get(),cache.Get(),&parent));item=parent;
                }
            }
            Require(!request.Guard||request.Guard->Current(),HRESULT_FROM_WIN32(ERROR_CANCELLED));result.Complete=true;
        }catch(const Failure& error){result.Error=error.Code;}catch(...){result.Error=E_FAIL;}
        result.Milliseconds=GetTickCount64()-started;return result;
    }
    void Run() noexcept{
        auto initialized=CoInitializeEx(nullptr,COINIT_MULTITHREADED);SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        ComPtr<IUIAutomation2> automation;auto status=FAILED(initialized)?initialized:CoCreateInstance(CLSID_CUIAutomation8,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&automation));
        if(SUCCEEDED(status))status=automation->put_ConnectionTimeout(100);if(SUCCEEDED(status))status=automation->put_TransactionTimeout(50);
        while(true){
            std::unique_lock lock(Gate);Changed.wait(lock,[&]{return Closed||Pending!=nullptr;});if(Closed)break;auto request=std::move(Pending);Active=true;SamplingSince=GetTickCount64();lock.unlock();
            PreviewViewItems result;if(FAILED(status))result.Error=status;else result=Sample(automation.Get(),*request);try{request->Result.set_value(std::move(result));}catch(...){}
            SamplingSince=0;lock.lock();Active=false;
        }
        automation.Reset();if(SUCCEEDED(initialized))CoUninitialize();
    }
public:
    ViewSampler():Worker([this]{Run();}){}
    ~ViewSampler(){{std::lock_guard lock(Gate);Closed=true;Changed.notify_all();}if(Worker.joinable())Worker.join();}
    PreviewViewItems Read(HWND window,const RECT& viewport,const std::wstring& directory,ULONGLONG deadline,std::shared_ptr<PreviewViewGuard> guard){
        auto request=std::make_shared<Request>();request->Window=window;request->Viewport=viewport;request->Directory=directory;request->Deadline=deadline;request->Guard=std::move(guard);auto result=request->Result.get_future();
        {std::lock_guard lock(Gate);if(Active||Pending){PreviewViewItems busy;busy.Error=HRESULT_FROM_WIN32(ERROR_BUSY);return busy;}Pending=request;Changed.notify_one();}
        const auto now=GetTickCount64();
        if(result.wait_for(std::chrono::milliseconds(deadline>now?deadline-now:0))!=std::future_status::ready){PreviewViewItems timeout;timeout.Error=HRESULT_FROM_WIN32(ERROR_TIMEOUT);timeout.Milliseconds=deadline>now?deadline-now:0;return timeout;}
        return result.get();
    }
};
}
bool PreviewSamplerUncooperative()noexcept{const auto started=SamplingSince.load();return started&&GetTickCount64()-started>=1000;}
PreviewViewItems ReadVisiblePreviewItems(IShellView* view,IFolderView2* folder,const std::wstring& directory,ULONGLONG deadline,std::shared_ptr<PreviewViewGuard> guard){
    if(!deadline)deadline=GetTickCount64()+250;
    UNREFERENCED_PARAMETER(folder);PreviewViewItems result;static ViewSampler sampler;
    try{
        Require(!guard||guard->Current(),HRESULT_FROM_WIN32(ERROR_CANCELLED));
        HWND window{};Check(view->GetWindow(&window));Require(window&&IsWindowVisible(window)&&!IsIconic(window));RECT viewport{};WinCheck(GetClientRect(window,&viewport));MapWindowPoints(window,nullptr,reinterpret_cast<POINT*>(&viewport),2);
        if(GetTickCount64()>=deadline){result.Error=HRESULT_FROM_WIN32(ERROR_TIMEOUT);return result;}
        result=sampler.Read(window,viewport,directory,deadline,std::move(guard));
    }catch(const Failure& error){result.Error=error.Code;}catch(...){result.Error=E_FAIL;}
    return result;
}
}

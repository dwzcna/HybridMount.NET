#include "VisiblePreviewItems.h"
#include "NativeFile.h"
#include <UIAutomation.h>
#include <wrl.h>
#include <set>
using Microsoft::WRL::ComPtr;
namespace hm {
namespace {
bool MediaName(const std::wstring& name){auto dot=name.find_last_of(L'.');if(dot==std::wstring::npos)return false;auto extension=name.substr(dot);for(auto& value:extension)value=static_cast<wchar_t>(towlower(value));for(auto item:{L".jpg",L".jpeg",L".png",L".bmp",L".gif",L".tif",L".tiff",L".heic",L".heif",L".webp",L".avif",L".mp4",L".exe"})if(extension==item)return true;return false;}
/// <summary>名称只作为根内单层本地提示；隐藏扩展名时最多检查八个同名候选，歧义不猜测。</summary>
std::wstring ResolveVisibleName(const std::wstring& directory,const std::wstring& name){
    if(name.empty()||name.size()>260||name==L"."||name==L".."||name.find_first_of(L"\\/:*?\"<>|")!=std::wstring::npos)return {};
    auto find=[&](const std::wstring& pattern){std::wstring selected;WIN32_FIND_DATAW data{};auto search=FindFirstFileExW((directory+L"\\"+pattern).c_str(),FindExInfoBasic,&data,FindExSearchNameMatch,nullptr,0);if(search==INVALID_HANDLE_VALUE)return selected;
        struct Search{HANDLE Value;~Search(){FindClose(Value);}} owner{search};int candidates{};do{if(++candidates>8)return std::wstring{};if((data.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)||!MediaName(data.cFileName))continue;if(!selected.empty())return std::wstring{};selected=data.cFileName;}while(FindNextFileW(search,&data));return selected;};
    auto exact=find(name);if(!exact.empty())return FullPath(directory+L"\\"+exact);if(MediaName(name))return {};auto extension=find(name+L".*");return extension.empty()?std::wstring{}:FullPath(directory+L"\\"+extension);
}
}
PreviewViewItems ReadVisiblePreviewItems(IShellView* view,IFolderView2* folder,const std::wstring& directory){
    PreviewViewItems result;try{
        HWND window{};Check(view->GetWindow(&window));Require(window&&IsWindowVisible(window)&&!IsIconic(window));RECT viewport{};WinCheck(GetWindowRect(window,&viewport));
        // UIA只读取已经呈现且与真实客户区相交的项目。禁止Realize、ScrollIntoView、焦点和选择写入。
        ComPtr<IUIAutomation> automation;Check(CoCreateInstance(CLSID_CUIAutomation,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&automation)));ComPtr<IUIAutomationElement> root;Check(automation->ElementFromHandle(window,&root));
        VARIANT value{};value.vt=VT_I4;value.lVal=UIA_ListItemControlTypeId;ComPtr<IUIAutomationCondition> list;Check(automation->CreatePropertyCondition(UIA_ControlTypePropertyId,value,&list));value.lVal=UIA_DataItemControlTypeId;ComPtr<IUIAutomationCondition> data;Check(automation->CreatePropertyCondition(UIA_ControlTypePropertyId,value,&data));ComPtr<IUIAutomationCondition> items;Check(automation->CreateOrCondition(list.Get(),data.Get(),&items));
        value.vt=VT_BOOL;value.boolVal=VARIANT_FALSE;ComPtr<IUIAutomationCondition> onScreen;Check(automation->CreatePropertyCondition(UIA_IsOffscreenPropertyId,value,&onScreen));ComPtr<IUIAutomationCondition> condition;Check(automation->CreateAndCondition(items.Get(),onScreen.Get(),&condition));
        ComPtr<IUIAutomationCacheRequest> cache;Check(automation->CreateCacheRequest(&cache));Check(cache->put_TreeScope(TreeScope_Element));for(auto property:{UIA_NamePropertyId,UIA_IsOffscreenPropertyId,UIA_BoundingRectanglePropertyId})Check(cache->AddProperty(property));
        ComPtr<IUIAutomationElementArray> elements;Check(root->FindAllBuildCache(TreeScope_Descendants,condition.Get(),cache.Get(),&elements));int count{};Check(elements->get_Length(&count));Require(count>=0&&count<=256,HRESULT_FROM_WIN32(ERROR_MORE_DATA));
        const auto deadline=GetTickCount64()+300;std::set<std::wstring> seen;int visible{};
        for(int index=0;index<count;++index){Require(GetTickCount64()<deadline,HRESULT_FROM_WIN32(ERROR_TIMEOUT));++result.Candidates;ComPtr<IUIAutomationElement> item;Check(elements->GetElement(index,&item));BOOL offScreen{};Check(item->get_CachedIsOffscreen(&offScreen));RECT rectangle{},intersection{};Check(item->get_CachedBoundingRectangle(&rectangle));if(offScreen||!IntersectRect(&intersection,&viewport,&rectangle))continue;++visible;BSTR raw{};Check(item->get_CachedName(&raw));std::wstring name(raw?raw:L"",raw?SysStringLen(raw):0);SysFreeString(raw);auto path=ResolveVisibleName(directory,name);if(!path.empty()&&path.size()<=4096&&seen.insert(path).second&&result.Paths.size()<64)result.Paths.push_back(Utf8(path));}
        int all{};Check(folder->ItemCount(SVGIO_ALLVIEW,&all));if(all>0&&visible==0){result.Error=HRESULT_FROM_WIN32(ERROR_RETRY);return result;}result.Complete=true;
    }catch(const Failure& error){result.Error=error.Code;}catch(...){result.Error=E_FAIL;}return result;
}
}

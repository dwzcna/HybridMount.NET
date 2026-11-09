#include "CloudProperties.h"
#include "RootRegistryGuard.h"
#include <propsys.h>
#include <wrl.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.Provider.h>
#include <charconv>
#include <set>

namespace hm {
namespace {
using Microsoft::WRL::ComPtr;
const GUID Format{0xfceff153,0xe839,0x4cf3,{0xa9,0xe7,0xea,0x22,0x83,0x20,0x94,0xb8}};
struct Value{PROPVARIANT Data{};~Value(){PropVariantClear(&Data);}};
ComPtr<IPropertyStore> Store(const std::wstring& path){ComPtr<IPropertyStore> store;Check(SHGetPropertyStoreFromParsingName(path.c_str(),nullptr,GPS_DEFAULT,IID_PPV_ARGS(&store)));return store;}
Json Strings(IPropertyStore* store,DWORD id){PROPERTYKEY key{Format,id};Value value;Check(store->GetValue(key,&value.Data));Json result=Json::array();if(value.Data.vt==VT_EMPTY)return result;Require(value.Data.vt==(VT_VECTOR|VT_LPWSTR)&&value.Data.calpwstr.cElems<=256&&(value.Data.calpwstr.cElems==0||value.Data.calpwstr.pElems));for(ULONG i=0;i<value.Data.calpwstr.cElems;++i){auto text=value.Data.calpwstr.pElems[i];std::wstring item=text?text:L"";Require(item.size()<=32768);result.push_back(Utf8(item));}return result;}
int PropertyId(const Json& value){auto text=value.get<std::string>();int id{};auto result=std::from_chars(text.data(),text.data()+text.size(),id);Require(!text.empty()&&text.front()>='0'&&text.front()<='9'&&result.ec==std::errc{}&&result.ptr==text.data()+text.size()&&id>=0);return id;}
}
Json ReadCustomProperties(const std::wstring& path){
    auto store=Store(path);PROPERTYKEY key{};Check(PSGetPropertyKeyFromName(L"System.StorageProviderCustomStates",&key));Value container;Check(store->GetValue(key,&container.Data));Json result={{"ids",Json::array()},{"values",Json::array()},{"icons",Json::array()},{"hasUnknownFields",false},{"providerId",nullptr}};if(container.Data.vt==VT_EMPTY)return result;
    Require(container.Data.vt==(VT_VECTOR|VT_UI1)&&container.Data.caub.cElems>0&&container.Data.caub.cElems<=1024*1024&&container.Data.caub.pElems);ComPtr<IPropertyStore> memory;Check(PSCreateMemoryPropertyStore(IID_PPV_ARGS(&memory)));ComPtr<IPersistSerializedPropStorage> persistence;Check(memory.As(&persistence));Check(persistence->SetPropertyStorage(reinterpret_cast<PCUSERIALIZEDPROPSTORAGE>(container.Data.caub.pElems),container.Data.caub.cElems));DWORD count{};Check(memory->GetCount(&count));Require(count<=256);
    bool unknown=false;for(DWORD i=0;i<count;++i){PROPERTYKEY current{};Check(memory->GetAt(i,&current));unknown|=current.fmtid!=Format||(current.pid!=119&&(current.pid<121||current.pid>123));}result["hasUnknownFields"]=unknown;result["ids"]=Strings(memory.Get(),121);result["values"]=Strings(memory.Get(),122);result["icons"]=Strings(memory.Get(),123);
    PROPERTYKEY provider{Format,119};Value value;Check(memory->GetValue(provider,&value.Data));if(value.Data.vt!=VT_EMPTY){Require(value.Data.vt==VT_LPWSTR&&value.Data.pwszVal);std::wstring text(value.Data.pwszVal);Require(text.size()<=2048);result["providerId"]=Utf8(text);}return result;
}
Json ReadShellProperties(const std::wstring& path){
    auto store=Store(path);Json result={{"providerState",nullptr},{"transferStatus",nullptr},{"placeholderStatus",nullptr},{"error",nullptr}};
    for(auto pair:{std::pair{"providerState",L"System.StorageProviderState"},std::pair{"transferStatus",L"System.SyncTransferStatus"},std::pair{"placeholderStatus",L"System.FilePlaceholderStatus"}}){PROPERTYKEY key{};Check(PSGetPropertyKeyFromName(pair.second,&key));Value value;Check(store->GetValue(key,&value.Data));if(value.Data.vt==VT_UI4||value.Data.vt==VT_I4)result[pair.first]=value.Data.ulVal;}return result;
}
DWORD ProviderState(const std::wstring& path){auto store=Store(path);PROPERTYKEY key{};Check(PSGetPropertyKeyFromName(L"System.StorageProviderState",&key));Value value;Check(store->GetValue(key,&value.Data));Require(value.Data.vt==VT_UI4);return value.Data.ulVal;}
bool SetCustomProperty(const Json& root,const std::wstring& path,bool directory,int owned,const std::wstring& text,const std::wstring& icon,HANDLE cancel){
    Require(owned==1||owned==2);auto stored=ReadCustomProperties(path);std::vector<int> ids;for(auto& item:stored.at("ids"))ids.push_back(PropertyId(item));const auto has=std::find(ids.begin(),ids.end(),owned)!=ids.end();if(!has&&text.empty())return false;Require(!stored.at("hasUnknownFields").get<bool>());
    Json properties=Json::array();if(std::any_of(ids.begin(),ids.end(),[&](int id){return id!=owned;})){
        Require(ids.size()==stored.at("values").size()&&ids.size()==stored.at("icons").size());std::set<int> unique(ids.begin(),ids.end());Require(unique.size()==ids.size());for(size_t i=0;i<ids.size();++i)if(ids[i]!=owned){auto value=stored.at("values")[i].get<std::string>(),resource=stored.at("icons")[i].get<std::string>();Require(!value.empty()&&!resource.empty());properties.push_back({{"id",ids[i]},{"value",value},{"icon",resource}});}
    }
    if(!text.empty())properties.push_back({{"id",owned},{"value",Utf8(text)},{"icon",Utf8(icon)}});
    bool same=ids.size()==properties.size()&&stored.at("values").size()==ids.size()&&stored.at("icons").size()==ids.size();if(same)for(size_t i=0;i<ids.size();++i)same&=properties[i].at("id")==ids[i]&&properties[i].at("value")==stored.at("values")[i]&&properties[i].at("icon")==stored.at("icons")[i];
    const auto provider=Utf8(ProductScope()+L"!"+CurrentSid()+L"!"+Wide(GuidN(root.at("mountId"))));if((!same||!text.empty())&&!ids.empty())Require(stored.at("providerId")==provider,E_ACCESSDENIED);if(same)return false;
    RootRegistryGuard propertyGuard(cancel);auto marker=RootMarker(path);Require(GuidN(marker.at("MountId"))==GuidN(root.at("mountId")),E_ACCESSDENIED);
    using namespace winrt::Windows::Storage;using namespace winrt::Windows::Storage::Provider;
    winrt::Windows::Storage::IStorageItem item{nullptr};if(directory)item=StorageFolder::GetFolderFromPathAsync(path).get();else item=StorageFile::GetFileFromPathAsync(path).get();auto vector=winrt::single_threaded_vector<StorageProviderItemProperty>();
    for(auto& property:properties){StorageProviderItemProperty value;value.Id(property.at("id").get<int>());value.Value(Text(property,"value"));value.IconResource(Text(property,"icon"));vector.Append(value);}StorageProviderItemProperties::SetAsync(item,vector).get();auto actual=ReadCustomProperties(path);Require(actual.at("providerId")==provider&&actual.at("ids").size()==properties.size()&&actual.at("values").size()==properties.size()&&actual.at("icons").size()==properties.size());
    for(size_t i=0;i<properties.size();++i)Require(actual.at("ids")[i]==std::to_string(properties[i].at("id").get<int>())&&actual.at("values")[i]==properties[i].at("value")&&actual.at("icons")[i]==properties[i].at("icon"));return true;
}
}

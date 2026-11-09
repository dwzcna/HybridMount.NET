#pragma once
#include "NativeCommon.h"
#include <winternl.h>
#include <cfapi.h>
#include <winioctl.h>

namespace hm {
/// <summary>不读正文的NTFS状态；Key与托管端现有LocalFileStamp编码相同。</summary>
struct FileState {
    std::string Identity;long long Usn{},Length{},LastWrite{},Change{};bool Directory{};DWORD Reasons{},Links{};
    std::string Key()const{return Identity+":"+std::to_string(Usn)+":"+std::to_string(Length)+":"+std::to_string(LastWrite)+":"+std::to_string(Change)+":"+(Directory?"True":"False");}
};
inline Handle OpenAttributes(const std::wstring& path,DWORD access=FILE_READ_ATTRIBUTES){
    auto handle=CreateFileW(path.c_str(),access,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_BACKUP_SEMANTICS,nullptr);
    if(handle==INVALID_HANDLE_VALUE)throw Failure(HRESULT_FROM_WIN32(GetLastError()));return Handle(handle);
}
inline FileState InspectFile(HANDLE handle){
    FILE_BASIC_INFO basic{};FILE_STANDARD_INFO standard{};FILE_ID_INFO id{};
    WinCheck(GetFileInformationByHandleEx(handle,FileBasicInfo,&basic,sizeof(basic)));WinCheck(GetFileInformationByHandleEx(handle,FileStandardInfo,&standard,sizeof(standard)));WinCheck(GetFileInformationByHandleEx(handle,FileIdInfo,&id,sizeof(id)));
    FileState result{Hex(&id,sizeof(id)),0,standard.EndOfFile.QuadPart,basic.LastWriteTime.QuadPart,basic.ChangeTime.QuadPart,standard.Directory!=FALSE,0,standard.NumberOfLinks};
    BYTE buffer[4096]{};WORD input[]{2,4};DWORD returned{};
    if(DeviceIoControl(handle,FSCTL_READ_FILE_USN_DATA,input,sizeof(input),buffer,sizeof(buffer),&returned,nullptr)&&returned>=32){
        WORD major{};memcpy(&major,buffer+4,2);
        if(major==2){memcpy(&result.Usn,buffer+24,8);if(returned>=44)memcpy(&result.Reasons,buffer+40,4);}
        else if(major==3&&returned>=48){memcpy(&result.Usn,buffer+40,8);if(returned>=60)memcpy(&result.Reasons,buffer+56,4);}
    }return result;
}
inline FileState InspectFile(const std::wstring& path){auto file=OpenAttributes(path);return InspectFile(file.Value);}
struct Placeholder {
    long long OnDisk{},Modified{},FileId{},RootFileId{};CF_PIN_STATE Pin{};CF_IN_SYNC_STATE InSync{};std::vector<BYTE> Identity;long long Validated{},Properties{};
};
inline Placeholder InspectPlaceholder(HANDLE handle){
    std::vector<BYTE> buffer(8192);DWORD returned{};Check(CfGetPlaceholderInfo(handle,CF_PLACEHOLDER_INFO_STANDARD,buffer.data(),static_cast<DWORD>(buffer.size()),&returned));
    Require(returned>=offsetof(CF_PLACEHOLDER_STANDARD_INFO,FileIdentity));auto info=reinterpret_cast<const CF_PLACEHOLDER_STANDARD_INFO*>(buffer.data());
    Require(info->FileIdentityLength>0&&info->FileIdentityLength<=4096&&offsetof(CF_PLACEHOLDER_STANDARD_INFO,FileIdentity)+info->FileIdentityLength<=returned);
    const auto bytes=static_cast<const BYTE*>(info->FileIdentity);return {info->OnDiskDataSize.QuadPart,info->ModifiedDataSize.QuadPart,info->FileId.QuadPart,info->SyncRootFileId.QuadPart,info->PinState,info->InSyncState,{bytes,bytes+info->FileIdentityLength},info->ValidatedDataSize.QuadPart,info->PropertiesSize.QuadPart};
}
inline bool HasFullContent(HANDLE handle,long long size){
    if(size==0)return true;Require(size>0);std::vector<CF_FILE_RANGE> ranges(4096);DWORD returned{};LARGE_INTEGER length{};length.QuadPart=size;
    Check(CfGetPlaceholderRangeInfo(handle,CF_PLACEHOLDER_RANGE_INFO_ONDISK,LARGE_INTEGER{},length,ranges.data(),static_cast<DWORD>(ranges.size()*sizeof(CF_FILE_RANGE)),&returned));
    Require(returned<=ranges.size()*sizeof(CF_FILE_RANGE)&&returned%sizeof(CF_FILE_RANGE)==0);long long end=0;
    for(DWORD i=0;i<returned/sizeof(CF_FILE_RANGE);++i){const auto start=ranges[i].StartingOffset.QuadPart,count=ranges[i].Length.QuadPart;Require(start>=0&&count>=0&&start<=LLONG_MAX-count);if(start>end)return false;end=std::max(end,start+count);}return end>=size;
}
inline Json RootMarker(const std::wstring& path){
    std::vector<BYTE> buffer(8192);DWORD returned{};Check(CfGetSyncRootInfoByPath(path.c_str(),CF_SYNC_ROOT_INFO_STANDARD,buffer.data(),static_cast<DWORD>(buffer.size()),&returned));
    Require(returned>=offsetof(CF_SYNC_ROOT_STANDARD_INFO,SyncRootIdentity));auto info=reinterpret_cast<const CF_SYNC_ROOT_STANDARD_INFO*>(buffer.data());
    Require(info->SyncRootIdentityLength>0&&info->SyncRootIdentityLength<=4096&&offsetof(CF_SYNC_ROOT_STANDARD_INFO,SyncRootIdentity)+info->SyncRootIdentityLength<=returned);
    auto marker=Parse(std::string(reinterpret_cast<const char*>(info->SyncRootIdentity),info->SyncRootIdentityLength),4096);
    Require(Text(marker,"Product")==ProductScope()&&Integer(marker,"Schema")==1&&Text(marker,"Owner")==CurrentSid(),E_ACCESSDENIED);GuidN(marker.at("MountId"));return marker;
}
inline bool SamePath(const std::wstring& a,const std::wstring& b){return CompareStringOrdinal(a.data(),static_cast<int>(a.size()),b.data(),static_cast<int>(b.size()),TRUE)==CSTR_EQUAL;}
inline std::wstring FullPath(const std::wstring& path){
    Require(path.size()>=3&&path.size()<32767&&path[1]==L':'&&path[2]==L'\\'&&path.find(L'\0')==std::wstring::npos&&path.find(L':',2)==std::wstring::npos);
    std::wstring result(32768,L'\0');auto count=GetFullPathNameW(path.c_str(),static_cast<DWORD>(result.size()),result.data(),nullptr);Require(count>0&&count<result.size());result.resize(count);while(result.size()>3&&result.back()==L'\\')result.pop_back();return result;
}
/// <summary>从根到目标逐段拒绝非云重解析点；数据库目录使用allowCloud=false。</summary>
inline void NoLinks(const std::wstring& path,bool allowCloud){
    const auto full=FullPath(path);size_t position=3;
    while(true){auto end=full.find(L'\\',position);auto current=end==std::wstring::npos?full:full.substr(0,end);auto file=OpenAttributes(current);FILE_ATTRIBUTE_TAG_INFO tag{};WinCheck(GetFileInformationByHandleEx(file.Value,FileAttributeTagInfo,&tag,sizeof(tag)));
        Require(!(tag.FileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)||(allowCloud&&((tag.ReparseTag&0xffff0fff)==0x9000001a)),E_ACCESSDENIED);if(end==std::wstring::npos)break;position=end+1;
    }
}
inline void ValidatePath(const Json& root,const std::wstring& path){
    const auto base=FullPath(Text(root,"Path")),full=FullPath(path);Require(full.size()>base.size()&&full[base.size()]==L'\\'&&SamePath(full.substr(0,base.size()),base),E_ACCESSDENIED);
    NoLinks(full,true);Require(InspectFile(base).Identity==root.at("FileId").get<std::string>(),E_ACCESSDENIED);
    auto actual=RootMarker(full);Require(GuidN(actual.at("MountId"))==GuidN(root.at("MountId"))&&Text(root,"Owner")==CurrentSid(),E_ACCESSDENIED);
}
}

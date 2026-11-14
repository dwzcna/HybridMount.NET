#include "CloudFileOperations.h"
#include "CloudProperties.h"
#include "LocalThumbnail.h"
#include <winioctl.h>

namespace hm {
namespace {
struct ProtectedFile {
    HANDLE Value{};HANDLE Raw{};bool Referenced{};
    ProtectedFile(const std::wstring& path,CF_OPEN_FILE_FLAGS flags){Check(CfOpenFileWithOplock(path.c_str(),flags,&Value));Referenced=CfReferenceProtectedHandle(Value)!=FALSE;if(!Referenced){CfCloseHandle(Value);Value=nullptr;throw Failure(HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION));}Raw=CfGetWin32HandleFromProtectedHandle(Value);}
    ~ProtectedFile(){if(Referenced)CfReleaseProtectedHandle(Value);if(Value)CfCloseHandle(Value);}
};
std::wstring EntryPath(const Json& data,bool exists=true){
    ValidateRuntimeRoot(data);auto root=FullPath(Text(data,"rootPath")),path=FullPath(Text(data,"path"));Require(path.size()>root.size()&&path[root.size()]==L'\\'&&SamePath(path.substr(0,root.size()),root),E_ACCESSDENIED);
    NoLinks(exists?path:path.substr(0,path.find_last_of(L'\\')),true);auto marker=RootMarker(exists?path:path.substr(0,path.find_last_of(L'\\')));Require(GuidN(marker.at("MountId"))==GuidN(data.at("mountId")),E_ACCESSDENIED);return path;
}
std::optional<Placeholder> Maybe(HANDLE handle){try{return InspectPlaceholder(handle);}catch(const Failure& error){if(error.Code==static_cast<HRESULT>(0x80070178)||error.Code==static_cast<HRESULT>(0x80070182)||error.Code==E_INVALIDARG)return {};throw;}}
void Identity(const Json& data,const Placeholder& info){auto marker=Parse(std::string(info.Identity.begin(),info.Identity.end()),4096);Require(GuidN(marker.at("MountId"))==GuidN(data.at("mountId"))&&marker.at("EntryId")==data.at("entryId"),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));}
void SameContent(const FileState& before,const FileState& after){Require(before.Identity==after.Identity&&before.Length==after.Length&&before.LastWrite==after.LastWrite&&before.Directory==after.Directory,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));}
void Clean(const Placeholder& info){Require(info.Modified==0&&info.InSync==CF_IN_SYNC_STATE_IN_SYNC,HRESULT_FROM_WIN32(ERROR_CLOUD_FILE_NOT_IN_SYNC));}
void UpdateMetadata(ProtectedFile& file,const Json& data){
    CF_FS_METADATA metadata{};metadata.FileSize.QuadPart=Number(data,"size");Require(metadata.FileSize.QuadPart>=0);metadata.BasicInfo.LastWriteTime.QuadPart=Number(data,"time");
    if(metadata.FileSize.QuadPart==0){WinCheck(GetFileInformationByHandleEx(file.Raw,FileBasicInfo,&metadata.BasicInfo,sizeof(metadata.BasicInfo)));metadata.BasicInfo.LastWriteTime.QuadPart=Number(data,"time");}
    CF_FILE_RANGE range{};range.Length.QuadPart=-1;USN usn{};Check(CfUpdatePlaceholder(file.Value,&metadata,nullptr,0,metadata.FileSize.QuadPart==0?nullptr:&range,metadata.FileSize.QuadPart==0?0:1,static_cast<CF_UPDATE_FLAGS>(metadata.FileSize.QuadPart==0?259:7),&usn,nullptr));
}
void Hydrate(RuntimeHost& host,RuntimeCommand& command,ProtectedFile& file){
    Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));WinCheck(event.Value!=nullptr);OVERLAPPED overlap{};overlap.hEvent=event.Value;LARGE_INTEGER length{};length.QuadPart=-1;
    command.CheckCancel();auto code=CfHydratePlaceholder(file.Value,LARGE_INTEGER{},length,CF_HYDRATE_FLAG_NONE,&overlap);
    if(code!=HRESULT_FROM_WIN32(ERROR_IO_PENDING)){Check(code);return;}
    const auto deadline=command.Operation=="applyRemoteSnapshot"?GetTickCount64()+120000:ULLONG_MAX;
    while(WaitForSingleObject(event.Value,100)==WAIT_TIMEOUT){const auto timedOut=GetTickCount64()>=deadline;if((command.Canceled()&&!command.Irreversible)||host.IsStopping()||timedOut){CancelIoEx(file.Raw,&overlap);DWORD count{};GetOverlappedResult(file.Raw,&overlap,&count,TRUE);throw Failure(HRESULT_FROM_WIN32(timedOut?ERROR_TIMEOUT:ERROR_OPERATION_ABORTED));}}
    DWORD count{};if(!GetOverlappedResult(file.Raw,&overlap,&count,FALSE)){command.CheckCancel();throw Failure(HRESULT_FROM_WIN32(GetLastError()));}command.CheckCancel();
}
/// <summary>保护句柄的随机I/O使用低位事件，避免把完成包交给CFAPI所属完成端口。</summary>
DWORD FileIo(HANDLE file,void* bytes,DWORD count,long long offset,bool write){
    Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));WinCheck(event.Value!=nullptr);OVERLAPPED io{};io.Offset=static_cast<DWORD>(offset);io.OffsetHigh=static_cast<DWORD>(static_cast<unsigned long long>(offset)>>32);io.hEvent=reinterpret_cast<HANDLE>(reinterpret_cast<ULONG_PTR>(event.Value)|1);DWORD done{};
    if(!(write?WriteFile(file,bytes,count,&done,&io):ReadFile(file,bytes,count,&done,&io))){auto code=GetLastError();Require(code==ERROR_IO_PENDING,HRESULT_FROM_WIN32(code));WinCheck(GetOverlappedResult(file,&io,&done,TRUE));}return done;
}
Handle Snapshot(const std::wstring& path){NoLinks(path,false);auto raw=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_OVERLAPPED|FILE_FLAG_SEQUENTIAL_SCAN,nullptr);if(raw==INVALID_HANDLE_VALUE)throw Failure(HRESULT_FROM_WIN32(GetLastError()));return Handle(raw);}
std::string HashFile(HANDLE file,long long length){
    Require(length>=0);BCRYPT_HASH_HANDLE hash{};Check(HRESULT_FROM_NT(BCryptCreateHash(BCRYPT_SHA256_ALG_HANDLE,&hash,nullptr,0,nullptr,0,0)));struct Hash{BCRYPT_HASH_HANDLE Value;~Hash(){BCryptDestroyHash(Value);}} owner{hash};std::vector<BYTE> buffer(256*1024);long long position=0;
    while(position<length){auto count=static_cast<DWORD>(std::min<long long>(buffer.size(),length-position));auto read=FileIo(file,buffer.data(),count,position,false);Require(read>0&&read<=count,HRESULT_FROM_WIN32(ERROR_HANDLE_EOF));Check(HRESULT_FROM_NT(BCryptHashData(hash,buffer.data(),read,0)));position+=read;}
    BYTE result[32]{};Check(HRESULT_FROM_NT(BCryptFinishHash(hash,result,sizeof(result),0)));return Hex(result,sizeof(result));
}
void WriteContent(RuntimeHost& host,RuntimeCommand& command,ProtectedFile& file,HANDLE source,long long length,long long time){
    std::vector<BYTE> buffer(256*1024);long long position=0;while(position<length){auto count=static_cast<DWORD>(std::min<long long>(buffer.size(),length-position));auto read=FileIo(source,buffer.data(),count,position,false);Require(read>0&&read<=count,HRESULT_FROM_WIN32(ERROR_HANDLE_EOF));DWORD done=0;while(done<read){auto written=FileIo(file.Raw,buffer.data()+done,read-done,position+done,true);Require(written>0&&written<=read-done);done+=written;}position+=read;host.Checkpoint(command,"writing",Json::object(),false,true);}
    FILE_END_OF_FILE_INFO end{};end.EndOfFile.QuadPart=length;WinCheck(SetFileInformationByHandle(file.Raw,FileEndOfFileInfo,&end,sizeof(end)));FILE_BASIC_INFO basic{};basic.LastWriteTime.QuadPart=time;WinCheck(SetFileInformationByHandle(file.Raw,FileBasicInfo,&basic,sizeof(basic)));WinCheck(FlushFileBuffers(file.Raw));
}
Json Create(const Json& data){
    auto path=EntryPath(data,false),parent=path.substr(0,path.find_last_of(L'\\')),name=path.substr(path.find_last_of(L'\\')+1);Require(!name.empty()&&name!=L"."&&name!=L"..");
    const auto directory=data.at("directory").get<bool>();auto time=Number(data,"time"),size=Number(data,"size");Require(size>=0&&time>=0);
    auto bytes=Json{{"MountId",data.at("mountId")},{"EntryId",data.at("entryId")}}.dump();CF_PLACEHOLDER_CREATE_INFO item{};item.RelativeFileName=name.c_str();item.FsMetadata.BasicInfo.CreationTime.QuadPart=time;item.FsMetadata.BasicInfo.LastWriteTime.QuadPart=time;item.FsMetadata.BasicInfo.LastAccessTime.QuadPart=time;item.FsMetadata.BasicInfo.ChangeTime.QuadPart=time;item.FsMetadata.BasicInfo.FileAttributes=directory?FILE_ATTRIBUTE_DIRECTORY:FILE_ATTRIBUTE_NORMAL;item.FsMetadata.FileSize.QuadPart=size;item.FileIdentity=bytes.data();item.FileIdentityLength=static_cast<DWORD>(bytes.size());item.Flags=static_cast<CF_PLACEHOLDER_CREATE_FLAGS>(directory?3:2);
    DWORD processed{};Check(CfCreatePlaceholders(parent.c_str(),&item,1,CF_CREATE_FLAG_STOP_ON_ERROR,&processed));Check(item.Result);Require(processed==1);return {{"stamp",FileJson(InspectFile(path))},{"createUsn",std::to_string(item.CreateUsn)}};
}
/// <summary>同一会话最多256项；逐父目录调用SDK批次，结果独立返回，已有文件绝不覆盖。</summary>
Json CreateBatch(const Json& data){
    ValidateRuntimeRoot(data);const auto& incoming=data.at("entries");Require(incoming.is_array()&&!incoming.empty()&&incoming.size()<=256);
    struct Pending{Json Data;std::wstring Path,Parent,Name;std::string Identity;};
    std::vector<Pending> pending;pending.reserve(incoming.size());std::map<std::wstring,std::vector<size_t>> groups;
    for(const auto& value:incoming){
        Require(GuidN(value.at("mountId"))==GuidN(data.at("mountId"))&&SamePath(FullPath(Text(value,"rootPath")),FullPath(Text(data,"rootPath")))&&value.at("rootIdentity")==data.at("rootIdentity"),E_ACCESSDENIED);
        auto path=EntryPath(value,false),parent=path.substr(0,path.find_last_of(L'\\')),name=path.substr(path.find_last_of(L'\\')+1);Require(!name.empty()&&name!=L"."&&name!=L"..");
        groups[parent].push_back(pending.size());pending.push_back({value,path,parent,name,Json{{"MountId",value.at("mountId")},{"EntryId",value.at("entryId")}}.dump()});
    }
    Json results=Json::array();
    for(const auto& [parent,indices]:groups){
        std::vector<CF_PLACEHOLDER_CREATE_INFO> entries(indices.size());
        for(size_t position=0;position<indices.size();++position){
            auto& value=pending[indices[position]];auto& item=entries[position];const auto directory=value.Data.at("directory").get<bool>();auto time=Number(value.Data,"time"),size=Number(value.Data,"size");Require(size>=0&&time>=0);
            item.RelativeFileName=value.Name.c_str();item.FsMetadata.BasicInfo.CreationTime.QuadPart=time;item.FsMetadata.BasicInfo.LastWriteTime.QuadPart=time;item.FsMetadata.BasicInfo.LastAccessTime.QuadPart=time;item.FsMetadata.BasicInfo.ChangeTime.QuadPart=time;
            item.FsMetadata.BasicInfo.FileAttributes=directory?FILE_ATTRIBUTE_DIRECTORY:FILE_ATTRIBUTE_NORMAL;item.FsMetadata.FileSize.QuadPart=size;item.FileIdentity=value.Identity.data();item.FileIdentityLength=static_cast<DWORD>(value.Identity.size());item.Flags=static_cast<CF_PLACEHOLDER_CREATE_FLAGS>(directory?3:2);item.Result=E_PENDING;
        }
        DWORD processed{};const auto overall=CfCreatePlaceholders(parent.c_str(),entries.data(),static_cast<DWORD>(entries.size()),CF_CREATE_FLAG_NONE,&processed);
        for(size_t position=0;position<indices.size();++position){
            auto& value=pending[indices[position]];auto& item=entries[position];auto code=item.Result==E_PENDING?(FAILED(overall)?overall:E_ABORT):item.Result;Json result{{"entryId",value.Data.at("entryId")},{"hresult",static_cast<int>(code)}};
            if(SUCCEEDED(code))try{auto file=OpenAttributes(value.Path);auto info=InspectPlaceholder(file.Value);Identity(value.Data,info);result["stamp"]=FileJson(InspectFile(file.Value));result["createUsn"]=std::to_string(item.CreateUsn);}catch(const Failure& error){result["hresult"]=static_cast<int>(error.Code);}
            results.push_back(std::move(result));
        }
    }
    return {{"results",std::move(results)}};
}
}
Json ExecuteCloudFile(RuntimeHost& host,RuntimeCommand& command,CloudSession* session,bool& handled){
    handled=true;auto& data=command.Data;const auto& operation=command.Operation;
    if(operation=="renderLocalThumbnail"){
        Require(session!=nullptr);auto path=EntryPath(data);
        // 属性探针必须在解码前关闭，避免其延迟最终只读USN更新并使回执戳落后于响应后的文件。
        {auto file=OpenAttributes(path);auto info=InspectPlaceholder(file.Value);Identity(data,info);auto before=InspectFile(file.Value);Require(!before.Directory&&HasFullContent(file.Value,before.Length),HRESULT_FROM_WIN32(ERROR_NOT_FOUND));}
        command.CheckCancel();std::string stamp;LocalThumbnailTiming timing;Json frames;auto png=DecodeLocalThumbnail(path,command.Cancel.Value,stamp,timing,&frames);command.CheckCancel();
        Require(!png.empty()&&LocalThumbnailStamp(path)==stamp,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));return {{"frames",frames},{"stamp",FileJson(InspectFile(path))},{"startupMs",timing.Startup},{"decodeMs",timing.Decode}};
    }
    if(operation=="createPlaceholder")return Create(data);
    if(operation=="createPlaceholders"){Require(session!=nullptr);return CreateBatch(data);}
    if(operation=="applyExclusion"){
        auto path=EntryPath(data);
        {
            auto probe=OpenAttributes(path);auto stamp=InspectFile(probe.Value);if(stamp.Directory)return {{"changed",false},{"stamp",FileJson(stamp)}};auto info=Maybe(probe.Value);if(info){auto marker=Parse(std::string(info->Identity.begin(),info->Identity.end()),4096);Require(GuidN(marker.at("MountId"))==GuidN(data.at("mountId")),E_ACCESSDENIED);}
            FILE_ATTRIBUTE_TAG_INFO tag{};WinCheck(GetFileInformationByHandleEx(probe.Value,FileAttributeTagInfo,&tag,sizeof(tag)));auto pin=info?info->Pin:static_cast<CF_PIN_STATE>(((tag.FileAttributes&FILE_ATTRIBUTE_PINNED)?1:0)|((tag.FileAttributes&FILE_ATTRIBUTE_UNPINNED)?2:0));auto deferred=data.at("excluded").get<bool>()&&info&&((CfGetPlaceholderStateFromAttributeTag(tag.FileAttributes,tag.ReparseTag)&CF_PLACEHOLDER_STATE_PARTIAL)||!HasFullContent(probe.Value,stamp.Length));
            auto decision=host.Checkpoint(command,"exclusionProbe",{{"stamp",FileJson(stamp)},{"pinState",pin},{"deferred",deferred}});if(!decision.value("proceed",false))return {{"changed",false},{"stamp",FileJson(stamp)}};
        }
        auto raw=CreateFileW(path.c_str(),FILE_READ_ATTRIBUTES|FILE_WRITE_ATTRIBUTES|DELETE,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_BACKUP_SEMANTICS,nullptr);if(raw==INVALID_HANDLE_VALUE)throw Failure(HRESULT_FROM_WIN32(GetLastError()));Handle file(raw);
        auto before=InspectFile(raw);Require(!before.Directory);auto info=Maybe(raw);if(info){auto marker=Parse(std::string(info->Identity.begin(),info->Identity.end()),4096);Require(GuidN(marker.at("MountId"))==GuidN(data.at("mountId")),E_ACCESSDENIED);}
        FILE_ATTRIBUTE_TAG_INFO tag{};WinCheck(GetFileInformationByHandleEx(raw,FileAttributeTagInfo,&tag,sizeof(tag)));auto pin=info?info->Pin:static_cast<CF_PIN_STATE>(((tag.FileAttributes&FILE_ATTRIBUTE_PINNED)?1:0)|((tag.FileAttributes&FILE_ATTRIBUTE_UNPINNED)?2:0));
        auto excluded=data.at("excluded").get<bool>();auto deferred=excluded&&info&&((CfGetPlaceholderStateFromAttributeTag(tag.FileAttributes,tag.ReparseTag)&CF_PLACEHOLDER_STATE_PARTIAL)||!HasFullContent(raw,before.Length));
        auto permission=host.Checkpoint(command,"exclusionGuarded",{{"stamp",FileJson(before)},{"pinState",pin},{"deferred",deferred}});if(!permission.value("proceed",false))return {{"changed",false},{"stamp",FileJson(before)}};
        auto desired=permission.at("desired").get<CF_PIN_STATE>();Require(desired>=CF_PIN_STATE_UNSPECIFIED&&desired<=CF_PIN_STATE_INHERIT);bool written=false;
        try{
            host.Checkpoint(command,"exclusionBegin",Json::object(),true);
            if(permission.at("changePin").get<bool>()){Check(CfSetPinState(raw,desired,CF_SET_PIN_FLAG_NONE,nullptr));written=true;}
            if(excluded){auto current=Maybe(raw);FILE_ATTRIBUTE_TAG_INFO actual{};WinCheck(GetFileInformationByHandleEx(raw,FileAttributeTagInfo,&actual,sizeof(actual)));auto result=current?current->Pin:static_cast<CF_PIN_STATE>(((actual.FileAttributes&FILE_ATTRIBUTE_PINNED)?1:0)|((actual.FileAttributes&FILE_ATTRIBUTE_UNPINNED)?2:0));Require(result==desired&& (info.has_value()||ProviderState(path)==9));}
            written|=SetCustomProperty(data,path,false,2,Text(data,"text"),Text(data,"icon"),command.Irreversible?host.StopHandle():command.Cancel.Value);if(written){DWORD count{};USN usn{};WinCheck(DeviceIoControl(raw,FSCTL_WRITE_USN_CLOSE_RECORD,nullptr,0,&usn,sizeof(usn),&count,nullptr));}
            auto after=InspectFile(raw);auto current=Maybe(raw);SameContent(before,after);Require(info.has_value()==current.has_value());if(info)Require(info->Identity==current->Identity&&info->InSync==current->InSync&&info->OnDisk==current->OnDisk);
            host.Checkpoint(command,"exclusionCommit",{{"stamp",FileJson(after)},{"changed",written}},false,true);return {{"changed",written},{"stamp",FileJson(after)}};
        }catch(...){auto error=std::current_exception();try{auto after=InspectFile(raw);SameContent(before,after);auto current=Maybe(raw);Require(info.has_value()==current.has_value());if(info)Require(info->Identity==current->Identity&&info->InSync==current->InSync&&info->OnDisk==current->OnDisk);host.Checkpoint(command,"exclusionProof",{{"stamp",FileJson(after)},{"changed",written||before.Key()!=after.Key()}},false,true);}catch(...){}std::rethrow_exception(error);}
    }
    if(operation=="setProperty"||operation=="publishIssue"){
        auto path=EntryPath(data);auto probe=OpenAttributes(path);auto originalInfo=Maybe(probe.Value);if(originalInfo){auto marker=Parse(std::string(originalInfo->Identity.begin(),originalInfo->Identity.end()),4096);Require(GuidN(marker.at("MountId"))==GuidN(data.at("mountId")),E_ACCESSDENIED);}
        auto raw=CreateFileW(path.c_str(),FILE_READ_ATTRIBUTES|FILE_WRITE_ATTRIBUTES|DELETE,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_BACKUP_SEMANTICS,nullptr);if(raw==INVALID_HANDLE_VALUE)throw Failure(HRESULT_FROM_WIN32(GetLastError()));Handle file(raw);auto before=InspectFile(raw);auto info=Maybe(raw);
        if(operation=="publishIssue"){
            Require(before.Identity==data.at("expectedIdentity").get<std::string>(),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));auto proceed=host.Checkpoint(command,"propertyGuarded",{{"stamp",FileJson(before)}});if(!proceed.value("proceed",false))return {{"changed",false},{"applied",false}};
        }
        if(operation=="publishIssue")host.Checkpoint(command,"propertyBegin",Json::object(),true);
        bool changed=false;try{command.CheckCancel();changed=SetCustomProperty(data,path,before.Directory,data.at("propertyId").get<int>(),Text(data,"text"),Text(data,"icon"),command.Irreversible?host.StopHandle():command.Cancel.Value);if(changed){DWORD count{};USN usn{};WinCheck(DeviceIoControl(raw,FSCTL_WRITE_USN_CLOSE_RECORD,nullptr,0,&usn,sizeof(usn),&count,nullptr));}
        auto after=InspectFile(raw);auto final=Maybe(raw);SameContent(before,after);Require(info.has_value()==final.has_value());if(info)Require(info->Identity==final->Identity&&info->InSync==final->InSync&&info->Pin==final->Pin&&info->OnDisk==final->OnDisk);
        if(operation=="publishIssue"){host.Checkpoint(command,"propertyWritten",{{"before",FileJson(before)},{"after",FileJson(after)}},false,true);host.Checkpoint(command,"propertyCommit",{{"before",FileJson(before)},{"after",FileJson(after)}},false,true);}
        return {{"changed",changed},{"applied",true},{"before",FileJson(before)},{"after",FileJson(after)}};
        }catch(...){auto error=std::current_exception();try{auto after=InspectFile(raw);auto final=Maybe(raw);SameContent(before,after);Require(info.has_value()==final.has_value());if(info)Require(info->Identity==final->Identity&&info->InSync==final->InSync&&info->Pin==final->Pin&&info->OnDisk==final->OnDisk);if(operation=="publishIssue")host.Checkpoint(command,"propertyProof",{{"before",FileJson(before)},{"after",FileJson(after)}},false,true);}catch(...){}std::rethrow_exception(error);}
    }
    if(operation=="replaceConflict"){
        auto path=EntryPath(data);const auto& original=data.at("original");const auto originalLength=Number(original,"length"),size=Number(data,"size");auto backup=Snapshot(Text(data,"backupPath")),snapshot=Snapshot(Text(data,"snapshotPath"));Require(InspectFile(backup.Value).Length==originalLength&&HashFile(backup.Value,originalLength)==data.at("backupSha256").get<std::string>());Require(InspectFile(snapshot.Value).Length==size&&HashFile(snapshot.Value,size)==data.at("sha256").get<std::string>());
        ProtectedFile file(path,CF_OPEN_FILE_FLAG_EXCLUSIVE|CF_OPEN_FILE_FLAG_WRITE_ACCESS);auto before=InspectFile(file.Raw);Require(before.Links==1&&!before.Directory&&before.Key()==data.at("originalKey").get<std::string>(),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));auto info=Maybe(file.Raw);if(info){Identity(data,*info);Require(HasFullContent(file.Raw,before.Length));}Require(HashFile(file.Raw,before.Length)==data.at("backupSha256").get<std::string>(),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        host.Checkpoint(command,"beginWrite",{{"stamp",FileJson(before)}},true);WriteContent(host,command,file,snapshot.Value,size,Number(data,"time"));Require(HashFile(file.Raw,size)==data.at("sha256").get<std::string>());
        if(!info){auto identity=Json{{"MountId",data.at("mountId")},{"EntryId",data.at("entryId")}}.dump();USN usn{};Check(CfConvertToPlaceholder(file.Value,identity.data(),static_cast<DWORD>(identity.size()),CF_CONVERT_FLAG_NONE,&usn,nullptr));}
        USN usn{};Check(CfSetInSyncState(file.Value,CF_IN_SYNC_STATE_IN_SYNC,CF_SET_IN_SYNC_FLAG_NONE,&usn));auto after=InspectFile(file.Raw);Require(before.Identity==after.Identity);if(info)Require(InspectPlaceholder(file.Raw).Pin==info->Pin);host.Checkpoint(command,"commit",{{"stamp",FileJson(after)}},false,true);return {{"stamp",FileJson(after)}};
    }
    if(operation=="applyRemoteSnapshot"){
        auto path=EntryPath(data);Require(session!=nullptr&&!data.at("protected").get<bool>()&&!data.at("deleted").get<bool>()&&!data.at("directory").get<bool>(),E_ACCESSDENIED);auto size=Number(data,"size"),time=Number(data,"time");auto snapshot=Snapshot(Text(data,"snapshotPath"));Require(InspectFile(snapshot.Value).Length==size&&HashFile(snapshot.Value,size)==data.at("sha256").get<std::string>());
        const auto recovery=data.at("recovering").get<bool>();if(!recovery)Require(!session->Active(data.at("entryId")),HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION));ProtectedFile file(path,CF_OPEN_FILE_FLAG_EXCLUSIVE|CF_OPEN_FILE_FLAG_WRITE_ACCESS);auto local=InspectFile(file.Raw);auto info=InspectPlaceholder(file.Raw);Identity(data,info);Clean(info);Require(!local.Directory);
        auto original=data.at("originalStamp").get<std::string>();const auto expectedId=original.substr(0,original.find(':'));const auto recovering=recovery&&local.Identity==expectedId&&local.Length==size&&local.LastWrite==time&&info.Pin==data.at("pinState").get<CF_PIN_STATE>();Require(local.Key()==original||recovering,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        host.Checkpoint(command,"installRemote",Json::object());bool restore=false;
        try{
            if(!recovery){host.Checkpoint(command,"remoteCommitting",{{"pinState",info.Pin}},true);restore=info.Pin==CF_PIN_STATE_PINNED;if(restore)Check(CfSetPinState(file.Value,CF_PIN_STATE_UNPINNED,CF_SET_PIN_FLAG_NONE,nullptr));UpdateMetadata(file,data);host.Checkpoint(command,"remoteHydrating",{{"stamp",FileJson(InspectFile(file.Raw))},{"pinState",info.Pin}},false,true);if(restore){Check(CfSetPinState(file.Value,CF_PIN_STATE_PINNED,CF_SET_PIN_FLAG_NONE,nullptr));restore=false;}}
            if(recovery)host.Checkpoint(command,"remoteHydrating",{{"stamp",FileJson(InspectFile(file.Raw))},{"pinState",info.Pin}},true,true);
            if(size!=0)Hydrate(host,command,file);Require(HashFile(file.Raw,size)==data.at("sha256").get<std::string>());auto final=InspectPlaceholder(file.Raw);Require(final.Modified==0&&final.Pin==(recovery?data.at("pinState").get<CF_PIN_STATE>():info.Pin));USN usn{};Check(CfSetInSyncState(file.Value,CF_IN_SYNC_STATE_IN_SYNC,CF_SET_IN_SYNC_FLAG_NONE,&usn));auto stamp=InspectFile(file.Raw);host.Checkpoint(command,"remoteCompleted",{{"stamp",FileJson(stamp)}},false,true);return {{"stamp",FileJson(stamp)}};
        }catch(...){if(restore)CfSetPinState(file.Value,CF_PIN_STATE_PINNED,CF_SET_PIN_FLAG_NONE,nullptr);throw;}
    }
    if(operation=="commitUpload"){
        auto path=EntryPath(data);ProtectedFile file(path,CF_OPEN_FILE_FLAG_EXCLUSIVE|CF_OPEN_FILE_FLAG_WRITE_ACCESS);auto stamp=InspectFile(file.Raw);
        if(stamp.Directory!=data.at("directory").get<bool>())return {{"committed",false},{"reason","type"},{"typeConflict",true},{"stamp",FileJson(stamp)}};
        auto expected=data.at("snapshotIdentity");if(expected.is_null()||stamp.Identity!=expected.get<std::string>())return {{"committed",false},{"reason","identity"},{"stamp",FileJson(stamp)}};
        auto info=Maybe(file.Raw);Json marker;
        if(info){marker=Parse(std::string(info->Identity.begin(),info->Identity.end()),4096);Require(GuidN(marker.at("MountId"))==GuidN(data.at("mountId")),E_ACCESSDENIED);}
        if(!stamp.Directory&&(data.at("snapshotStamp").is_null()||stamp.Key()!=data.at("snapshotStamp").get<std::string>())){
            if(data.at("verifiedStamp").is_null()||stamp.Key()!=data.at("verifiedStamp").get<std::string>())return {{"committed",false},{"reason","stamp"},{"stamp",FileJson(stamp)}};
        }
        auto identity=Json{{"MountId",data.at("mountId")},{"EntryId",data.at("entryId")}}.dump();USN usn=stamp.Usn;
        if(!info)Check(CfConvertToPlaceholder(file.Value,identity.data(),static_cast<DWORD>(identity.size()),CF_CONVERT_FLAG_NONE,&usn,nullptr));
        else{
            Require(GuidN(marker.at("MountId"))==GuidN(data.at("mountId")),E_ACCESSDENIED);
            if(marker.at("EntryId")!=data.at("entryId")){usn=0;Check(CfUpdatePlaceholder(file.Value,nullptr,identity.data(),static_cast<DWORD>(identity.size()),nullptr,0,CF_UPDATE_FLAG_MARK_IN_SYNC,&usn,nullptr));}
        }
        usn=0;Check(CfSetInSyncState(file.Value,CF_IN_SYNC_STATE_IN_SYNC,CF_SET_IN_SYNC_FLAG_NONE,&usn));return {{"committed",true},{"stamp",FileJson(InspectFile(file.Raw))}};
    }
    if(operation=="deleteRemote"){
        Require(!data.at("protected").get<bool>()&&!data.at("deleted").get<bool>(),E_ACCESSDENIED);auto path=EntryPath(data);auto before=InspectFile(path);if(session)Require(!session->Active(data.at("entryId")),HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION));
        if(before.Directory){WIN32_FIND_DATAW item{};auto find=FindFirstFileW((path+L"\\*").c_str(),&item);Require(find!=INVALID_HANDLE_VALUE,HRESULT_FROM_WIN32(GetLastError()));bool empty=true;do{if(wcscmp(item.cFileName,L".")&&wcscmp(item.cFileName,L"..")){empty=false;break;}}while(FindNextFileW(find,&item));FindClose(find);Require(empty,HRESULT_FROM_WIN32(ERROR_DIR_NOT_EMPTY));}
        ProtectedFile file(path,CF_OPEN_FILE_FLAG_EXCLUSIVE|CF_OPEN_FILE_FLAG_DELETE_ACCESS);auto current=InspectFile(file.Raw);Require(current.Key()==before.Key(),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));auto info=InspectPlaceholder(file.Raw);Identity(data,info);if(!current.Directory)Clean(info);command.CheckCancel();FILE_DISPOSITION_INFO_EX disposition{};disposition.Flags=FILE_DISPOSITION_FLAG_DELETE|FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;WinCheck(SetFileInformationByHandle(file.Raw,FileDispositionInfoEx,&disposition,sizeof(disposition)));return Json::object();
    }
    if(operation=="detachExcluded"){
        auto path=EntryPath(data);ProtectedFile file(path,CF_OPEN_FILE_FLAG_EXCLUSIVE|CF_OPEN_FILE_FLAG_WRITE_ACCESS);auto before=InspectFile(file.Raw);const auto& proof=data.at("proofStamp");Require(!before.Directory&&before.Identity==data.at("fileId").get<std::string>()&&before.Identity==proof.at("identity").get<std::string>()&&before.Length==Number(proof,"length")&&before.LastWrite==Number(proof,"lastWriteTime")&&(before.Usn==Number(proof,"usn")||(before.Reasons&0x77)==0)&&before.Links==1,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        auto info=Maybe(file.Raw);if(info){auto marker=Parse(std::string(info->Identity.begin(),info->Identity.end()),4096);Require(GuidN(marker.at("MountId"))==GuidN(data.at("mountId"))&&(marker.at("EntryId")==data.at("entryId")||marker.at("EntryId")==data.at("uploadId")),E_ACCESSDENIED);Require(HasFullContent(file.Raw,before.Length));Check(CfRevertPlaceholder(file.Value,CF_REVERT_FLAG_NONE,nullptr));}auto after=InspectFile(file.Raw);SameContent(before,after);Require(!Maybe(file.Raw).has_value());return {{"stamp",FileJson(after)}};
    }
    // 取消固定只写单文件属性；UNSPECIFIED保留已水合范围，不调用CfDehydratePlaceholder。
    if(operation=="cancelPin"){
        auto path=EntryPath(data);auto file=OpenAttributes(path,FILE_READ_ATTRIBUTES|FILE_WRITE_ATTRIBUTES);auto before=InspectFile(file.Value);auto info=InspectPlaceholder(file.Value);Identity(data,info);
        Require(!before.Directory&&before.Identity==data.at("fileId").get<std::string>(),HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        Require(info.Pin!=CF_PIN_STATE_EXCLUDED,E_ACCESSDENIED);command.CheckCancel();
        if(info.Pin==CF_PIN_STATE_PINNED)Check(CfSetPinState(file.Value,CF_PIN_STATE_UNSPECIFIED,CF_SET_PIN_FLAG_NONE,nullptr));
        SameContent(before,InspectFile(file.Value));return {{"stamp",FileJson(InspectFile(file.Value))}};
    }
    if(operation=="requestPin"||operation=="requestUnpin"){
        auto path=EntryPath(data);auto file=OpenAttributes(path,FILE_READ_ATTRIBUTES|FILE_WRITE_ATTRIBUTES);auto before=InspectFile(file.Value);auto info=InspectPlaceholder(file.Value);Identity(data,info);Require(info.Pin!=CF_PIN_STATE_EXCLUDED,E_ACCESSDENIED);Check(CfSetPinState(file.Value,operation=="requestPin"?CF_PIN_STATE_PINNED:CF_PIN_STATE_UNPINNED,CF_SET_PIN_FLAG_NONE,nullptr));SameContent(before,InspectFile(file.Value));return Json::object();
    }
    if(operation=="refreshPlaceholder"){
        auto path=EntryPath(data);Require(session!=nullptr);Require(!session->Active(data.at("entryId")),HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION));ProtectedFile file(path,CF_OPEN_FILE_FLAG_EXCLUSIVE);auto info=InspectPlaceholder(file.Raw);Identity(data,info);Clean(info);Require(info.Pin==CF_PIN_STATE_UNSPECIFIED||info.Pin==CF_PIN_STATE_UNPINNED,HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION));UpdateMetadata(file,data);return {{"stamp",FileJson(InspectFile(file.Raw))}};
    }
    if(operation=="hydrateFile"||operation=="hydratePinned"){
        auto path=EntryPath(data);ProtectedFile file(path,operation=="hydratePinned"?CF_OPEN_FILE_FLAG_EXCLUSIVE:CF_OPEN_FILE_FLAG_WRITE_ACCESS|CF_OPEN_FILE_FLAG_FOREGROUND);auto before=InspectFile(file.Raw);auto info=InspectPlaceholder(file.Raw);Identity(data,info);
        if(operation=="hydratePinned"){Clean(info);Require(info.Pin==CF_PIN_STATE_PINNED);host.Checkpoint(command,"pinnedGuarded",{{"stamp",FileJson(before)}});}
        bool notified=false;auto releasing=[&]{if(operation=="hydratePinned"&&!notified){notified=true;host.Checkpoint(command,"pinReleasing",Json::object(),false,true);}};
        try{Hydrate(host,command,file);auto after=InspectFile(file.Raw),pathState=InspectFile(path);auto final=InspectPlaceholder(file.Raw);SameContent(before,after);Require(after.Identity==pathState.Identity&&final.Identity==info.Identity&&HasFullContent(file.Raw,after.Length));if(operation=="hydratePinned"){Clean(final);Require(final.Pin==CF_PIN_STATE_PINNED);}releasing();return {{"stamp",FileJson(after)}};}
        catch(...){auto error=std::current_exception();try{releasing();}catch(...){}std::rethrow_exception(error);}
    }

    if(operation=="dehydratePending"||operation=="releaseFile"||operation=="trimFile"){
        auto path=EntryPath(data);Require(session!=nullptr);auto attributes=OpenAttributes(path);auto observed=InspectPlaceholder(attributes.Value);Identity(data,observed);Clean(observed);if(operation=="dehydratePending"&&observed.Pin!=CF_PIN_STATE_UNPINNED)return {{"changed",false}};Require(observed.Pin!=CF_PIN_STATE_EXCLUDED,E_ACCESSDENIED);FILE_ATTRIBUTE_TAG_INFO tag{};WinCheck(GetFileInformationByHandleEx(attributes.Value,FileAttributeTagInfo,&tag,sizeof(tag)));if(observed.OnDisk==0&&(CfGetPlaceholderStateFromAttributeTag(tag.FileAttributes,tag.ReparseTag)&CF_PLACEHOLDER_STATE_PARTIAL)!=0)return {{"changed",false}};
        auto before=InspectFile(attributes.Value);ProtectedFile file(path,CF_OPEN_FILE_FLAG_EXCLUSIVE);auto local=InspectFile(file.Raw);Require(local.Key()==before.Key()&&!session->Active(data.at("entryId")),HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION));auto info=InspectPlaceholder(file.Raw);Identity(data,info);Clean(info);Require(info.Pin!=CF_PIN_STATE_EXCLUDED,E_ACCESSDENIED);
        if(operation=="dehydratePending")Require(info.Pin==CF_PIN_STATE_UNPINNED);else if(operation=="trimFile")Require(info.Pin==CF_PIN_STATE_UNSPECIFIED||info.Pin==CF_PIN_STATE_UNPINNED);else Check(CfSetPinState(file.Value,CF_PIN_STATE_UNPINNED,CF_SET_PIN_FLAG_NONE,nullptr));
        if(!local.Directory){LARGE_INTEGER length{};length.QuadPart=-1;Check(CfDehydratePlaceholder(file.Value,LARGE_INTEGER{},length,CF_DEHYDRATE_FLAG_BACKGROUND,nullptr));}
        auto after=InspectPlaceholder(file.Raw);Clean(after);Require(after.Pin==(operation=="trimFile"?info.Pin:CF_PIN_STATE_UNPINNED)&&after.Identity==info.Identity);return {{"changed",true},{"freed",std::to_string(std::max(0LL,info.OnDisk-after.OnDisk))},{"stamp",FileJson(InspectFile(file.Raw))}};
    }
    // 缓存保留不执行水合：不完整项必须仍干净；完整项仅解除占位，正文不变。
    if(operation=="preserveCachedLocal"){
        Require(session!=nullptr&&session->IsClearing(),E_ACCESSDENIED);
        const auto discard=data.at("discard").get<bool>();auto path=EntryPath(data);
        ProtectedFile file(path,CF_OPEN_FILE_FLAG_EXCLUSIVE|(discard?CF_OPEN_FILE_FLAG_DELETE_ACCESS:CF_OPEN_FILE_FLAG_WRITE_ACCESS));
        auto before=InspectFile(file.Raw);Require(before.Key()==data.at("proofStamp").get<std::string>()&&before.Links==1,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));
        auto info=InspectPlaceholder(file.Raw);Identity(data,info);Require(!session->Active(data.at("entryId")),HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION));
        const auto full=before.Directory||HasFullContent(file.Raw,before.Length);
        Require(discard?!full:full,HRESULT_FROM_WIN32(ERROR_FILE_INVALID));command.CheckCancel();
        if(discard){
            Clean(info);Require(!data.at("protected").get<bool>()&&!data.at("deleted").get<bool>(),E_ACCESSDENIED);
            FILE_DISPOSITION_INFO_EX disposition{};disposition.Flags=FILE_DISPOSITION_FLAG_DELETE|FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
            WinCheck(SetFileInformationByHandle(file.Raw,FileDispositionInfoEx,&disposition,sizeof(disposition)));return {{"discarded",true}};
        }
        Check(CfRevertPlaceholder(file.Value,CF_REVERT_FLAG_NONE,nullptr));
        auto after=InspectFile(file.Raw);SameContent(before,after);Require(!Maybe(file.Raw).has_value());return {{"discarded",false},{"stamp",FileJson(after)}};
    }
    if(operation=="revertLocal"){
        auto path=EntryPath(data);ProtectedFile file(path,CF_OPEN_FILE_FLAG_EXCLUSIVE|CF_OPEN_FILE_FLAG_WRITE_ACCESS);auto before=InspectFile(file.Raw);auto info=Maybe(file.Raw);if(info){Identity(data,*info);Require(before.Directory||HasFullContent(file.Raw,before.Length));command.CheckCancel();Check(CfRevertPlaceholder(file.Value,CF_REVERT_FLAG_NONE,nullptr));}auto after=InspectFile(file.Raw);SameContent(before,after);Require(!Maybe(file.Raw).has_value());return {{"stamp",FileJson(after)}};
    }
    handled=false;return Json::object();
}
}

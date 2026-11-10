#include "PreviewWorker.h"
#include "NativeWire.h"
#include <wrl.h>
#include <objidl.h>
#include <mutex>
#include <optional>
using Microsoft::WRL::ComPtr;
namespace hm {
namespace {
struct Channel {std::mutex Gate;HANDLE Input=GetStdHandle(STD_INPUT_HANDLE),Output=GetStdHandle(STD_OUTPUT_HANDLE);};
/// <summary>WIC/MF只读IStream；每个读取串行提交至父进程，不能打开任意路径或URL。</summary>
class BrokerStream final:public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,IStream>{
    std::shared_ptr<Channel> _channel;long long _length,_position{};
public:
    BrokerStream(std::shared_ptr<Channel> channel,long long length,long long position=0):_channel(std::move(channel)),_length(length),_position(position){}
    IFACEMETHODIMP Read(void* buffer,ULONG count,ULONG* read)override{
        if(read)*read=0;if(!buffer&&count)return STG_E_INVALIDPOINTER;
        try{
            auto size=static_cast<DWORD>(std::min<long long>(count,_length-_position));if(!size)return count?S_FALSE:S_OK;
            std::lock_guard lock(_channel->Gate);DWORD copied{};while(copied<size){auto take=std::min<DWORD>(size-copied,256*1024);WriteFrame(_channel->Output,{{"kind","read"},{"offset",_position},{"count",take}});auto response=ReadFrame(_channel->Input);Require(response.at("kind")=="data");auto bytes=Base64(response.at("data").get<std::string>());Require(bytes.size()==take);memcpy(static_cast<BYTE*>(buffer)+copied,bytes.data(),take);_position+=take;copied+=take;if(read)*read=copied;}return size==count?S_OK:S_FALSE;
        }catch(const Failure& error){return error.Code;}catch(...){return E_FAIL;}
    }
    IFACEMETHODIMP Seek(LARGE_INTEGER move,DWORD origin,ULARGE_INTEGER* position)override{
        const auto base=origin==STREAM_SEEK_SET?0:origin==STREAM_SEEK_CUR?_position:origin==STREAM_SEEK_END?_length:-1;
        if(base<0||move.QuadPart< -base||move.QuadPart>_length-base)return STG_E_INVALIDFUNCTION;_position=base+move.QuadPart;if(position)position->QuadPart=_position;return S_OK;
    }
    IFACEMETHODIMP Stat(STATSTG* stat,DWORD)override{if(!stat)return E_POINTER;*stat={};stat->type=STGTY_STREAM;stat->cbSize.QuadPart=_length;stat->grfMode=STGM_READ;return S_OK;}
    IFACEMETHODIMP Clone(IStream** result)override{if(!result)return E_POINTER;auto clone=Microsoft::WRL::Make<BrokerStream>(_channel,_length,_position);return clone?clone.CopyTo(result):E_OUTOFMEMORY;}
    IFACEMETHODIMP Write(const void*,ULONG,ULONG*)override{return STG_E_ACCESSDENIED;}
    IFACEMETHODIMP SetSize(ULARGE_INTEGER)override{return STG_E_ACCESSDENIED;}
    IFACEMETHODIMP CopyTo(IStream*,ULARGE_INTEGER,ULARGE_INTEGER*,ULARGE_INTEGER*)override{return E_NOTIMPL;}
    IFACEMETHODIMP Commit(DWORD)override{return S_OK;}
    IFACEMETHODIMP Revert()override{return E_NOTIMPL;}
    IFACEMETHODIMP LockRegion(ULARGE_INTEGER,ULARGE_INTEGER,DWORD)override{return E_NOTIMPL;}
    IFACEMETHODIMP UnlockRegion(ULARGE_INTEGER,ULARGE_INTEGER,DWORD)override{return E_NOTIMPL;}
};
std::vector<BYTE> ReadAt(IStream* stream,long long offset,DWORD length){
    LARGE_INTEGER seek{};seek.QuadPart=offset;Check(stream->Seek(seek,STREAM_SEEK_SET,nullptr));std::vector<BYTE> bytes(length);DWORD done=0;
    while(done<length){ULONG read{};Check(stream->Read(bytes.data()+done,length-done,&read));Require(read>0);done+=read;}return bytes;
}
WORD U16(const std::vector<BYTE>& data,size_t offset){Require(offset<=data.size()&&data.size()-offset>=2);WORD result{};memcpy(&result,data.data()+offset,2);return result;}
DWORD U32(const std::vector<BYTE>& data,size_t offset){Require(offset<=data.size()&&data.size()-offset>=4);DWORD result{};memcpy(&result,data.data()+offset,4);return result;}
std::vector<BYTE> Resource(const std::vector<BYTE>& bytes,DWORD rootRva,DWORD type,std::optional<DWORD> name){
    auto child=[&](DWORD directory,std::optional<DWORD> id,bool sub){
        Require(directory<=bytes.size()&&bytes.size()-directory>=16);auto count=DWORD(U16(bytes,directory+12))+U16(bytes,directory+14);Require(count<=4096&&directory+16ULL+count*8ULL<=bytes.size());
        for(DWORD i=0;i<count;++i){auto position=directory+16+i*8;if(id&&U32(bytes,position)!=*id)continue;auto value=U32(bytes,position+4);Require(((value&0x80000000)!=0)==sub);return value&0x7fffffff;}throw Failure(E_FAIL);
    };
    auto data=child(child(child(0,type,true),name,true),std::nullopt,false);auto rva=U32(bytes,data),size=U32(bytes,data+4);Require(rva>=rootRva&&size>0&&size<=4*1024*1024);auto offset=static_cast<size_t>(rva-rootRva);Require(offset<=bytes.size()&&size<=bytes.size()-offset);return {bytes.begin()+offset,bytes.begin()+offset+size};
}
/// <summary>解析PE资源为内存ICO；不LoadLibrary目标、不执行PE入口或依赖。</summary>
ComPtr<IStream> ExecutableIcon(IStream* stream,UINT requested){
    auto dos=ReadAt(stream,0,64);Require(U16(dos,0)==IMAGE_DOS_SIGNATURE);auto pe=U32(dos,60);Require(pe>=64&&pe<=16*1024*1024);
    auto header=ReadAt(stream,pe,24);Require(U32(header,0)==IMAGE_NT_SIGNATURE);auto sections=U16(header,6),optionalSize=U16(header,20);Require(sections>0&&sections<=96&&optionalSize>=120&&optionalSize<=4096);
    auto optional=ReadAt(stream,pe+24,optionalSize);auto magic=U16(optional,0);Require(magic==IMAGE_NT_OPTIONAL_HDR32_MAGIC||magic==IMAGE_NT_OPTIONAL_HDR64_MAGIC);size_t directory=magic==IMAGE_NT_OPTIONAL_HDR32_MAGIC?96:112;
    auto rva=U32(optional,directory+16),size=U32(optional,directory+20);Require(size>=16&&size<=16*1024*1024);
    auto table=ReadAt(stream,pe+24+optionalSize,static_cast<DWORD>(sections)*40);std::optional<long long> start;
    for(WORD i=0;i<sections;++i){auto offset=i*40;auto sectionRva=U32(table,offset+12),rawSize=U32(table,offset+16),raw=U32(table,offset+20);if(rva>=sectionRva&&rva-sectionRva<rawSize){Require(size<=rawSize-(rva-sectionRva));start=static_cast<long long>(raw)+(rva-sectionRva);break;}}Require(start.has_value());
    auto resources=ReadAt(stream,*start,size);auto group=Resource(resources,rva,14,std::nullopt);Require(U16(group,2)==1);auto count=U16(group,4);Require(count>0&&count<=256&&group.size()>=6+static_cast<size_t>(count)*14);
    size_t selected=0;unsigned best=UINT_MAX;
    for(WORD i=0;i<count;++i){auto offset=6+static_cast<size_t>(i)*14;unsigned width=group[offset]?group[offset]:256;unsigned score=width>=requested?width-requested:1000+requested-width;if(score<best){selected=offset;best=score;}}
    auto data=Resource(resources,rva,3,U16(group,selected+12));std::vector<BYTE> ico(22+data.size());ico[2]=1;ico[4]=1;memcpy(ico.data()+6,group.data()+selected,8);auto bytes=static_cast<DWORD>(data.size());DWORD offset=22;memcpy(ico.data()+14,&bytes,4);memcpy(ico.data()+18,&offset,4);memcpy(ico.data()+22,data.data(),data.size());
    ComPtr<IStream> result;Check(CreateStreamOnHGlobal(nullptr,TRUE,&result));ULONG written{};Check(result->Write(ico.data(),static_cast<ULONG>(ico.size()),&written));Require(written==ico.size());Check(result->Seek(LARGE_INTEGER{},STREAM_SEEK_SET,nullptr));return result;
}
}
int RunPreviewWorker()noexcept{
    auto channel=std::make_shared<Channel>();const auto initialized=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    struct Scope{HRESULT Code;~Scope(){if(SUCCEEDED(Code))CoUninitialize();}} apartment{initialized};
    try{
        Check(initialized);WinCheck(SetPriorityClass(GetCurrentProcess(),PROCESS_MODE_BACKGROUND_BEGIN));auto request=ReadFrame(channel->Input);Require(request.at("kind")=="decode");const auto extension=Text(request,"extension");auto length=Integer(request,"length");Require(length>=0);
        bool supported=false;for(auto item:{L".jpg",L".jpeg",L".png",L".bmp",L".gif",L".tif",L".tiff",L".heic",L".heif",L".webp",L".avif",L".mp4",L".exe"})supported|=extension==item;Require(supported);
        auto module=LoadLibraryExW((ModuleDirectory()+L"\\HybridMount.Ext.dll").c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);Require(module!=nullptr);
        struct Module{HMODULE Value;~Module(){FreeLibrary(Value);}} owner{module};using Decode=HRESULT(WINAPI*)(IStream*,const wchar_t*,UINT,BYTE**,DWORD*);Decode decode{};auto proc=GetProcAddress(module,"HybridMountDecodePreview");Require(proc!=nullptr);memcpy(&decode,&proc,sizeof(decode));
        auto input=Microsoft::WRL::Make<BrokerStream>(channel,length);Require(input!=nullptr);Json frames=Json::object();
        // 原图或视频只提取一次；后续只解码内存中的256像素PNG，不再读取源流。
        ComPtr<IStream> content=input;if(extension==L".exe")content=ExecutableIcon(input.Get(),256);
        BYTE* primary{};DWORD primaryCount{};auto status=decode(content.Get(),extension==L".exe"?L".ico":extension.c_str(),256,&primary,&primaryCount);
        struct Memory{BYTE* Value;~Memory(){CoTaskMemFree(Value);}} original{primary};Check(status);Require(primary&&primaryCount>=8&&primaryCount<=384*1024);frames["256"]=Base64(primary,primaryCount);
        for(UINT size:{32u,64u,128u}){
            ComPtr<IStream> scaledInput;Check(CreateStreamOnHGlobal(nullptr,TRUE,&scaledInput));ULONG written{};Check(scaledInput->Write(primary,primaryCount,&written));Require(written==primaryCount);Check(scaledInput->Seek(LARGE_INTEGER{},STREAM_SEEK_SET,nullptr));
            BYTE* bytes{};DWORD count{};status=decode(scaledInput.Get(),L".png",size,&bytes,&count);Memory memory{bytes};Check(status);Require(bytes&&count>=8&&count<=384*1024);frames[std::to_string(size)]=Base64(bytes,count);
        }
        WriteFrame(channel->Output,{{"kind","result"},{"frames",frames}});return 0;
    }catch(const Failure& error){try{WriteFrame(channel->Output,{{"kind","unsupported"},{"hResult",error.Code}});}catch(...){}return 1;}catch(...){try{WriteFrame(channel->Output,{{"kind","unsupported"},{"hResult",E_FAIL}});}catch(...){}return 1;}
}
}

#include "RuntimeHost.h"
#include "CallbackMailbox.h"
#include "RuntimeOperations.h"
#include "NativeFile.h"
#include "Version.h"
#include "NativeDiagnostics.h"
#include <processthreadsapi.h>
#include <winrt/base.h>
#include <exception>
#include <new>
#include <algorithm>

namespace hm {
namespace {
std::wstring Family(HANDLE process){UINT32 count{};auto status=GetPackageFamilyName(process,&count,nullptr);if(status==APPMODEL_ERROR_NO_PACKAGE)return {};Require(status==ERROR_INSUFFICIENT_BUFFER&&count<=2048);std::wstring result(count,L'\0');Require(GetPackageFamilyName(process,&count,result.data())==ERROR_SUCCESS);result.resize(count-1);return result;}
[[maybe_unused]] std::wstring ProcessPath(HANDLE process){DWORD count=32768;std::wstring path(count,L'\0');WinCheck(QueryFullProcessImageNameW(process,0,path.data(),&count));path.resize(count);return path;}
std::wstring User(HANDLE process){HANDLE raw{};WinCheck(OpenProcessToken(process,TOKEN_QUERY,&raw));Handle token(raw);DWORD count{};GetTokenInformation(raw,TokenUser,nullptr,0,&count);Require(count>0&&count<65536);std::vector<BYTE> bytes(count);WinCheck(GetTokenInformation(raw,TokenUser,bytes.data(),count,&count));LPWSTR sid{};WinCheck(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(bytes.data())->User.Sid,&sid));std::wstring result(sid);LocalFree(sid);return result;}
struct Pipe {
    Handle File;HANDLE Parent,Stop;
    Pipe(const std::wstring& name,DWORD parent,HANDLE parentHandle,HANDLE stop):Parent(parentHandle),Stop(stop){
        const auto deadline=GetTickCount64()+10000;
        while(true){auto raw=CreateFileW(name.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED|SECURITY_SQOS_PRESENT|SECURITY_IDENTIFICATION,nullptr);
            if(raw!=INVALID_HANDLE_VALUE){File.Value=raw;break;}auto error=GetLastError();Require(error==ERROR_FILE_NOT_FOUND||error==ERROR_PIPE_BUSY,HRESULT_FROM_WIN32(error));Require(GetTickCount64()<deadline,HRESULT_FROM_WIN32(ERROR_TIMEOUT));WaitNamedPipeW(name.c_str(),100);
        }
        ULONG peer{};WinCheck(GetNamedPipeServerProcessId(File.Value,&peer));Require(peer==parent,E_ACCESSDENIED);
    }
    void Transfer(void* memory,DWORD length,bool write){auto data=static_cast<BYTE*>(memory);while(length){
        Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));WinCheck(event.Value!=nullptr);OVERLAPPED io{};io.hEvent=event.Value;DWORD count{};
        if(!(write?WriteFile(File.Value,data,length,&count,&io):ReadFile(File.Value,data,length,&count,&io))){auto code=GetLastError();Require(code==ERROR_IO_PENDING,HRESULT_FROM_WIN32(code));HANDLE waits[]{event.Value,Parent,Stop};auto done=WaitForMultipleObjects(3,waits,FALSE,INFINITE);if(done!=WAIT_OBJECT_0){CancelIoEx(File.Value,&io);GetOverlappedResult(File.Value,&io,&count,TRUE);throw Failure(HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED));}WinCheck(GetOverlappedResult(File.Value,&io,&count,FALSE));}
        Require(count>0,HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE));data+=count;length-=count;
    }}
    Json Read(){DWORD size{};Transfer(&size,4,false);Require(size>0&&size<=1024*1024);std::string bytes(size,'\0');Transfer(bytes.data(),size,false);return Parse(bytes,1024*1024);}
    void Write(const Json& value){auto bytes=value.dump();Require(!bytes.empty()&&bytes.size()<=1024*1024);auto size=static_cast<DWORD>(bytes.size());Transfer(&size,4,true);Transfer(bytes.data(),size,true);}
};
/// <summary>固定规模的原生执行池；每池至多512项。统计只读内存，不查询文件或生成缩略图。</summary>
class Pool {
    struct Item{std::function<void()> Work;ULONGLONG Queued;};
    std::mutex _gate;std::condition_variable _wake;std::deque<Item> _queue;bool _stop{};std::vector<std::thread> _workers;
    unsigned _active{};unsigned long long _accepted{},_rejected{},_completed{},_waitMs{},_runMs{},_maximumMs{},_slow{};
public:
    explicit Pool(int count=8){for(int i=0;i<count;++i)_workers.emplace_back([this]{while(true){Item item;{
        std::unique_lock lock(_gate);_wake.wait(lock,[this]{return _stop||!_queue.empty();});if(_queue.empty()&&_stop)return;
        item=std::move(_queue.front());_queue.pop_front();++_active;_waitMs+=GetTickCount64()-item.Queued;
        }const auto started=GetTickCount64();try{item.Work();}catch(...){}
        {std::lock_guard lock(_gate);--_active;++_completed;const auto elapsed=GetTickCount64()-started;_runMs+=elapsed;_maximumMs=std::max(_maximumMs,elapsed);if(elapsed>=250)++_slow;}
    }});}
    bool Add(std::function<void()> value){std::lock_guard lock(_gate);if(_stop||_queue.size()>=512){++_rejected;return false;}_queue.push_back({std::move(value),GetTickCount64()});++_accepted;_wake.notify_one();return true;}
    Json Snapshot(){std::lock_guard lock(_gate);return {{"threads",_workers.size()},{"queued",_queue.size()},{"active",_active},{"accepted",_accepted},{"rejected",_rejected},{"completed",_completed},{"waitMs",_waitMs},{"runMs",_runMs},{"maximumMs",_maximumMs},{"slow",_slow}};}
    void Stop(){{std::lock_guard lock(_gate);_stop=true;}_wake.notify_all();for(auto& thread:_workers)if(thread.joinable())thread.join();}
    ~Pool(){Stop();}
};
std::string GuidBytes(const BYTE* bytes){GUID id{};memcpy(&id,bytes,sizeof(id));wchar_t text[40]{};Require(StringFromGUID2(id,text,40)>0);return GuidN(Utf8(text));}
}
struct RuntimeHost::Impl {
    DWORD ParentId{};Handle Parent,Stop{CreateEventW(nullptr,TRUE,FALSE,nullptr)};std::unique_ptr<Pipe> Control,Data,Tasks;std::atomic<bool> Closed{};
    std::mutex OutGate,CallsGate,ChecksGate,DataWriteGate;std::condition_variable OutWake;std::deque<std::pair<Json,size_t>> Out;size_t OutBytes{};std::map<std::string,Json> PreviewOut;
    std::map<std::string,std::shared_ptr<RuntimeCommand>> Calls;std::map<std::string,std::shared_ptr<std::promise<Json>>> Checks;
    std::atomic<unsigned long long> CallbackCount{},CallbackSlow{},CallbackMaxMs{};
    CallbackMailbox Callbacks;Handle CallbackFailure{CreateEventW(nullptr,TRUE,FALSE,nullptr)};std::atomic<bool> CallbackFailed{};
#ifdef HYBRIDMOUNT_NATIVE_TESTS
    std::atomic<bool> CallbackPaused{};
#endif
    bool CallbackReady()const noexcept{
#ifdef HYBRIDMOUNT_NATIVE_TESTS
        if(CallbackPaused)return false;
#endif
        return Callbacks.Pending()!=0;
    }
    std::mutex TaskGate;std::condition_variable TaskWake;std::deque<std::pair<Json,size_t>> TaskOut;size_t TaskBytes{};
    Pool Workers,Fast{4},Long{4},Completion{2};
    void Close(const char* stage=nullptr,HRESULT code=S_OK){{std::lock_guard lock(OutGate);if(Closed.exchange(true))return;}if(stage&&code!=HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE)&&code!=HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED))RecordNativeFailure("NativeRuntime",code,stage);SetEvent(Stop.Value);Callbacks.Close();OutWake.notify_all();TaskWake.notify_all();{std::lock_guard lock(CallsGate);for(auto& [_,call]:Calls)SetEvent(call->Cancel.Value);}std::lock_guard lock(ChecksGate);for(auto& [_,promise]:Checks)try{promise->set_exception(std::make_exception_ptr(Failure(HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE))));}catch(...){}Checks.clear();}
};
RuntimeHost::RuntimeHost(DWORD parent,const std::wstring& nonce):_impl(std::make_unique<Impl>()){
    Require(parent>0&&nonce.size()==32);GuidN(Utf8(nonce));auto& p=*_impl;p.ParentId=parent;p.Parent.Value=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,parent);WinCheck(p.Parent.Value!=nullptr);
    auto authenticate=[](bool valid,const char* stage){if(!valid){RecordNativeFailure("NativeRuntime",E_ACCESSDENIED,stage);Require(false,E_ACCESSDENIED);}};
    DWORD selfSession{},peerSession{};WinCheck(ProcessIdToSessionId(GetCurrentProcessId(),&selfSession));WinCheck(ProcessIdToSessionId(parent,&peerSession));authenticate(peerSession==selfSession,"ParentSessionMismatch");authenticate(User(p.Parent.Value)==CurrentSid(),"ParentUserMismatch");authenticate(Family(p.Parent.Value)==Family(GetCurrentProcess()),"ParentPackageMismatch");
#ifndef HYBRIDMOUNT_NATIVE_TESTS
    authenticate(SamePath(ProcessPath(p.Parent.Value),ModuleDirectory()+L"\\HybridMount.exe"),"ParentImageMismatch");
#endif
    auto prefix=L"\\\\.\\pipe\\"+ProductScope()+L".runtime.v2."+CurrentSid()+L"."+std::to_wstring(selfSession)+L"."+nonce;
    p.Control=std::make_unique<Pipe>(prefix+L".control",parent,p.Parent.Value,p.Stop.Value);p.Data=std::make_unique<Pipe>(prefix+L".data",parent,p.Parent.Value,p.Stop.Value);p.Tasks=std::make_unique<Pipe>(prefix+L".tasks",parent,p.Parent.Value,p.Stop.Value);
    p.Control->Write({{"kind","hello"},{"version",2},{"nonce",Utf8(nonce)},{"scope",Utf8(ProductScope())},{"processId",GetCurrentProcessId()},{"build",HYBRIDMOUNT_VERSION_TEXT}});
    auto response=p.Control->Read();authenticate(response.contains("kind")&&response.at("kind")=="helloAck"&&response.contains("version")&&response.at("version")==2&&response.contains("nonce")&&response.at("nonce")==Utf8(nonce),"HandshakeMismatch");
    if(!response.contains("build")||response.at("build")!=HYBRIDMOUNT_VERSION_TEXT){auto actual=response.contains("build")&&response.at("build").is_string()?response.at("build").get<std::string>():std::string();RecordNativeFailure("NativeRuntime",E_ACCESSDENIED,"BuildMismatch",HYBRIDMOUNT_VERSION_TEXT,actual);Require(false,E_ACCESSDENIED);}
}
RuntimeHost::~RuntimeHost(){_impl->Close();_impl->Workers.Stop();_impl->Fast.Stop();_impl->Long.Stop();_impl->Completion.Stop();}
DWORD RuntimeHost::ParentProcessId()const noexcept{return _impl->ParentId;}
bool RuntimeHost::IsStopping()const noexcept{return _impl->Closed.load()||_impl->CallbackFailed.load();}
HANDLE RuntimeHost::StopHandle()const noexcept{return _impl->Stop.Value;}
void RuntimeHost::Close(const char* stage,HRESULT code){_impl->Close(stage,code);}
bool RuntimeHost::Send(Json value,bool waitForCapacity)noexcept{
    try{auto size=value.dump().size();auto& p=*_impl;std::unique_lock lock(p.OutGate);if(size>1024*1024)return false;
        if(waitForCapacity)p.OutWake.wait(lock,[&]{return p.Closed||(p.Out.size()<512&&p.OutBytes+size<=4*1024*1024);});
        if(p.Closed||p.Out.size()>=512||p.OutBytes+size>4*1024*1024)return false;p.OutBytes+=size;p.Out.emplace_back(std::move(value),size);p.OutWake.notify_all();return true;
    }catch(...){return false;}
}
bool RuntimeHost::SendTask(Json value)noexcept{
    try{auto bytes=value.dump().size();if(bytes>32768)return false;auto& p=*_impl;std::lock_guard lock(p.TaskGate);
        if(p.Closed||p.TaskOut.size()>=4096||p.TaskBytes+bytes>8*1024*1024)return false;
        p.TaskBytes+=bytes;p.TaskOut.emplace_back(std::move(value),bytes);p.TaskWake.notify_one();return true;
    }catch(...){return false;}
}
bool RuntimeHost::SendCallback(Json value,ULONGLONG deadline)noexcept{
    auto& p=*_impl;if(IsStopping())return false;const auto sent=p.Callbacks.Push(std::move(value),deadline);if(sent)p.OutWake.notify_all();else FailCallbacks();return sent;
}
void RuntimeHost::FailCallbacks()noexcept{_impl->CallbackFailed=true;SetEvent(_impl->CallbackFailure.Value);}
Json RuntimeHost::CallbackStatistics()const{auto value=_impl->Callbacks.Statistics();value["callbackCount"]=_impl->CallbackCount.load();value["callbackAbove50ms"]=_impl->CallbackSlow.load();value["callbackMaxMs"]=_impl->CallbackMaxMs.load();return value;}
void RuntimeHost::RecordCallbackTime(ULONGLONG elapsed)noexcept{auto& p=*_impl;++p.CallbackCount;if(elapsed>50)++p.CallbackSlow;auto high=p.CallbackMaxMs.load();while(high<elapsed&&!p.CallbackMaxMs.compare_exchange_weak(high,elapsed)){};}
#ifdef HYBRIDMOUNT_NATIVE_TESTS
void RuntimeHost::PauseCallbackWriterForTests(bool paused){_impl->CallbackPaused=paused;_impl->OutWake.notify_all();}
#endif
void RuntimeHost::SendPreview(Json value)noexcept {
    try{if(value.dump().size()>768*1024)return;auto key=value.at("mountId").get<std::string>();auto& p=*_impl;std::lock_guard lock(p.OutGate);if(p.Closed||p.PreviewOut.size()>=32&&!p.PreviewOut.contains(key))return;p.PreviewOut[key]=std::move(value);p.OutWake.notify_all();}catch(...){}
}
Json RuntimeHost::Checkpoint(RuntimeCommand& command,const std::string& stage,const Json& data,bool beginIrreversible,bool safety){
    if(!safety)command.CheckCancel();GUID id{};Check(CoCreateGuid(&id));wchar_t raw[40]{};Require(StringFromGUID2(id,raw,40)>0);const auto key=GuidN(Utf8(raw));auto promise=std::make_shared<std::promise<Json>>();auto future=promise->get_future();
    {std::lock_guard lock(_impl->ChecksGate);Require(!_impl->Closed,HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE));_impl->Checks.emplace(key,promise);}
    try{
        Require(Send({{"kind","checkpoint"},{"requestId",command.Id},{"checkpointId",key},{"stage",stage},{"beginIrreversible",beginIrreversible},{"safety",safety},{"data",data}},true),HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE));
        while(future.wait_for(std::chrono::milliseconds(100))!=std::future_status::ready){Require(!IsStopping(),HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE));/* 取消交给托管处理器合作完成；实际ACK之前不释放领域所有者。 */}
        {std::lock_guard lock(_impl->ChecksGate);_impl->Checks.erase(key);}auto result=future.get();Check(result.value("hresult",E_FAIL));if(beginIrreversible)command.Irreversible=true;if(!safety)command.CheckCancel();return result.value("data",Json::object());
    }catch(...){std::lock_guard lock(_impl->ChecksGate);_impl->Checks.erase(key);throw;}
}
void RuntimeHost::SendBlock(const std::string& request,const std::string& lease,long long offset,const std::vector<BYTE>& bytes){
    Require(bytes.size()<=256*1024&&offset>=0);auto guid=[](const std::string& value){auto n=GuidN(value);GUID result{};auto d=L"{"+Wide(n.substr(0,8)+"-"+n.substr(8,4)+"-"+n.substr(12,4)+"-"+n.substr(16,4)+"-"+n.substr(20))+L"}";Check(CLSIDFromString(d.c_str(),&result));return result;};auto id=guid(request),target=guid(lease);
    DWORD size=static_cast<DWORD>(40+bytes.size());std::vector<BYTE> frame(4+size);memcpy(frame.data(),&size,4);memcpy(frame.data()+4,&id,16);memcpy(frame.data()+20,&target,16);memcpy(frame.data()+36,&offset,8);if(!bytes.empty())memcpy(frame.data()+44,bytes.data(),bytes.size());std::lock_guard lock(_impl->DataWriteGate);_impl->Data->Transfer(frame.data(),static_cast<DWORD>(frame.size()),true);
}
void RuntimeHost::Run(){
    auto& p=*_impl;
    // 故障监视独立于管道发送与消费者，队列满也能唤醒停止和取消。
    std::thread failure([&]{HANDLE events[]{p.CallbackFailure.Value,p.Stop.Value,p.Parent.Value};if(WaitForMultipleObjects(3,events,FALSE,INFINITE)!=WAIT_OBJECT_0+1)p.Close("NativeCallbackQueue",HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA));});
    std::thread writer([&]{unsigned callbackRun=0;try{while(true){Json value;{std::unique_lock lock(p.OutGate);p.OutWake.wait(lock,[&]{return p.Closed||!p.Out.empty()||p.CallbackReady()||!p.PreviewOut.empty();});if(p.Closed)return;if(p.CallbackReady()&&(callbackRun<8||p.Out.empty())&&p.Callbacks.Pop(value)){++callbackRun;}else if(!p.Out.empty()){callbackRun=0;auto item=std::move(p.Out.front());p.Out.pop_front();p.OutBytes-=item.second;p.OutWake.notify_all();value=std::move(item.first);}else{auto item=p.PreviewOut.begin();value=std::move(item->second);p.PreviewOut.erase(item);}}p.Control->Write(value);}}catch(const Failure& error){p.Close("NativeControlWriter",error.Code);}catch(...){p.Close("NativeControlWriter",E_FAIL);}});
    std::thread data([&]{try{while(!p.Closed){DWORD length{};p.Data->Transfer(&length,4,false);Require(length>40&&length<=40+256*1024);std::vector<BYTE> bytes(length);p.Data->Transfer(bytes.data(),length,false);RuntimeBlock block{GuidBytes(bytes.data()),GuidBytes(bytes.data()+16)};memcpy(&block.Offset,bytes.data()+32,8);block.Bytes.assign(bytes.begin()+40,bytes.end());HRESULT code=S_OK;try{Require(Content!=nullptr);Content(block);}catch(const Failure& error){code=error.Code;}catch(...){code=E_FAIL;}Require(Send({{"kind","response"},{"requestId",block.Id},{"hresult",code},{"data",Json::object()}},true),E_FAIL);}}catch(const Failure& error){p.Close("NativeDataReader",error.Code);}catch(...){p.Close("NativeDataReader",E_FAIL);}});
    std::thread taskWriter([&]{try{while(true){Json frame;{std::unique_lock lock(p.TaskGate);p.TaskWake.wait(lock,[&]{return p.Closed||!p.TaskOut.empty();});if(p.Closed)return;auto item=std::move(p.TaskOut.front());p.TaskOut.pop_front();p.TaskBytes-=item.second;frame=std::move(item.first);}p.Tasks->Write(frame);}}catch(const Failure& error){p.Close("NativeTaskWriter",error.Code);}catch(...){p.Close("NativeTaskWriter",E_FAIL);}});
    std::thread taskReader([&]{try{while(!p.Closed){auto frame=p.Tasks->Read();Require(TaskMessage!=nullptr);TaskMessage(frame);}}catch(const Failure& error){p.Close("NativeTaskReader",error.Code);}catch(...){p.Close("NativeTaskReader",E_INVALIDARG);}});
    try{while(!p.Closed){auto frame=p.Control->Read();const auto kind=frame.at("kind").get<std::string>();
        if(kind=="checkpointAck"){auto id=GuidN(frame.at("checkpointId"));std::shared_ptr<std::promise<Json>> promise;{std::lock_guard lock(p.ChecksGate);auto found=p.Checks.find(id);if(found!=p.Checks.end()){promise=found->second;p.Checks.erase(found);}}if(promise)promise->set_value(std::move(frame));continue;}
        if(kind=="cancel"){auto id=GuidN(frame.at("requestId"));std::lock_guard lock(p.CallsGate);auto found=p.Calls.find(id);if(found!=p.Calls.end())SetEvent(found->second->Cancel.Value);continue;}
        Require(kind=="command");auto command=std::make_shared<RuntimeCommand>();command->Id=GuidN(frame.at("requestId"));command->Operation=frame.at("operation").get<std::string>();command->Data=frame.at("data");Require(command->Operation.size()<=64&&command->Data.is_object());
        {std::lock_guard lock(p.CallsGate);Require(p.Calls.emplace(command->Id,command).second);}
        const auto& op=command->Operation;bool completion=op=="stopTaskWatch"||op=="runtimeSchedulingDiagnostics"||op=="finishFetch"||op=="closeLocalRead"||op=="clearRoot"||op=="blockEntry";bool fast=op=="localReadDiagnostics"||op=="thumbnailDiagnostics"||op=="readShellState"||op=="readCustomState"||op=="queryFile"||op=="queryFiles"||op=="queryRoot"||op=="tagState"||op=="activity";
        bool longRunning=op=="renderLocalThumbnail"||op=="hydrateFile"||op=="hydratePinned"||op=="applyRemoteSnapshot";
#ifdef HYBRIDMOUNT_NATIVE_TESTS
        // 只在测试映像路由无文件的延迟命令，实际使用生产工作池及取消路径。
        if(op=="testPoolDelay"){const auto pool=command->Data.value("pool",std::string());fast=pool=="metadata";longRunning=pool=="long";completion=pool=="completion";}
        if(op=="testCompletion")completion=true;
#endif
        const auto accepted=(completion?p.Completion:fast?p.Fast:longRunning?p.Long:p.Workers).Add([&,command]{Json result=Json::object();HRESULT code=S_OK;try{command->CheckCancel();Require(Execute!=nullptr);result=command->Operation=="runtimeSchedulingDiagnostics"?Json{{"ordinary",p.Workers.Snapshot()},{"metadata",p.Fast.Snapshot()},{"long",p.Long.Snapshot()},{"completion",p.Completion.Snapshot()}}:Execute(*command);}catch(const Failure& error){code=error.Code;result["failureStage"]=error.Stage;}catch(const winrt::hresult_error& error){code=error.code();}catch(const Json::exception& error){code=E_INVALIDARG;result["failureStage"]="json:"+std::to_string(error.id);}catch(const std::bad_alloc&){code=E_OUTOFMEMORY;result["failureStage"]="out-of-memory";}catch(const std::exception&){code=E_FAIL;result["failureStage"]="std-exception";}catch(...){code=E_FAIL;}Send({{"kind","response"},{"requestId",command->Id},{"hresult",code},{"data",result}},true);std::lock_guard lock(p.CallsGate);p.Calls.erase(command->Id);});
        if(!accepted){if(!Send({{"kind","response"},{"requestId",command->Id},{"hresult",HRESULT_FROM_WIN32(ERROR_BUSY)},{"data",Json::object()}},false))p.Close("NativeReplyQueue",HRESULT_FROM_WIN32(ERROR_BUSY));std::lock_guard lock(p.CallsGate);p.Calls.erase(command->Id);}
    }}catch(const Failure& error){p.Close("NativeControlReader",error.Code);}
    catch(const Json::exception& error){auto stage="NativeControlReader.json:"+std::to_string(error.id);p.Close(stage.c_str(),E_INVALIDARG);}
    catch(const std::bad_alloc&){p.Close("NativeControlReader.out-of-memory",E_OUTOFMEMORY);}
    catch(const std::exception&){p.Close("NativeControlReader.std-exception",E_FAIL);}
    catch(...){p.Close("NativeControlReader",E_FAIL);}
    p.Close();if(Stopping)Stopping();p.Workers.Stop();p.Fast.Stop();p.Long.Stop();p.Completion.Stop();writer.join();data.join();taskWriter.join();taskReader.join();failure.join();
}
int RunRuntime(DWORD parent,const std::wstring& nonce)noexcept{
    const auto apartment=CoInitializeEx(nullptr,COINIT_MULTITHREADED);struct Scope{HRESULT Code;~Scope(){if(SUCCEEDED(Code))CoUninitialize();}} scope{apartment};
    try{Check(apartment);RuntimeHost host(parent,nonce);RuntimeOperations operations(host);host.Execute=[&](RuntimeCommand& call){return operations.Execute(call);};host.Content=[&](const RuntimeBlock& block){operations.Content(block);};host.TaskMessage=[&](const Json& frame){operations.TaskMessage(frame);};host.Stopping=[&]{operations.Stop();};host.Run();return 0;}catch(const Failure& error){RecordNativeFailure("NativeRuntime",error.Code);return static_cast<int>(error.Code);}catch(...){RecordNativeFailure("NativeRuntime",E_FAIL);return 1;}
}
}

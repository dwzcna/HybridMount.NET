#pragma once
#include "NativeFile.h"
#include <optional>
#include <map>
#include <set>
#include <functional>

namespace hm {
/// <summary>事件只做分类；后台事件和普通目录内容变化不进入采样队列。</summary>
enum class PreviewEventAction {Ignore,Content,Navigation};
inline PreviewEventAction ClassifyPreviewEvent(DWORD event,LONG object,LONG child,bool current,bool related,bool inView,bool viewWindow,bool owned){
    if(!current||!related)return PreviewEventAction::Ignore;
    const bool boundary=(event==EVENT_OBJECT_SHOW||event==EVENT_OBJECT_HIDE||event==EVENT_OBJECT_DESTROY||event==EVENT_OBJECT_PARENTCHANGE)&&object==OBJID_WINDOW&&child==CHILDID_SELF;
    if((boundary&&(!inView||viewWindow))||((event==EVENT_OBJECT_NAMECHANGE||event==EVENT_OBJECT_VALUECHANGE||event==EVENT_OBJECT_SELECTION)&&!inView))return PreviewEventAction::Navigation;
    return inView&&owned?PreviewEventAction::Content:PreviewEventAction::Ignore;
}
/// <summary>前台窗口的不可变身份；只含本地标识，进程创建时间防止PID/窗口句柄复用。</summary>
struct PreviewForeground {
    HWND Window{};DWORD Process{},Thread{};ULONGLONG Created{};
    bool operator==(const PreviewForeground&)const=default;
};
/// <summary>一次识别的身份、导航代次及绝对期限；控制线程是唯一写入者。</summary>
struct PreviewTicket {PreviewForeground Target;ULONGLONG Generation{},Deadline{};};
/// <summary>仅控制线程使用的前台采样策略；无Shell、磁盘或网络操作，失败保留同代需求。</summary>
class ForegroundPreviewState {
public:
    PreviewForeground Target;ULONGLONG Generation{},Due{},After{},Samples{},Late{},Timeouts{},Filtered{},DirectoryMs{},SampleMs{};
    unsigned Failures{};HRESULT LastError=S_OK;std::string LastStage;bool Dirty{},Known{};
    Json Value={{"complete",true},{"owned",false},{"paths",Json::array()}};
    /// <summary>切换身份立即清空旧需求；非Explorer前台不安排目录请求。</summary>
    bool Focus(PreviewForeground target,ULONGLONG now){
        if(Target==target)return false;Target=target;Invalidate(now);return true;
    }
    /// <summary>已确认导航使旧结果失效；滚动不调用本方法，避免把瞬时失败误作离开。</summary>
    void Invalidate(ULONGLONG now){
        ++Generation;Value={{"complete",true},{"owned",false},{"paths",Json::array()}};Known=false;Failures=0;After=0;LastError=S_OK;LastStage.clear();Dirty=Target.Window!=nullptr;Due=now+250;
    }
    /// <summary>合并连续变化而不不断延后期限；仅当前已匹配挂载的内容事件可进入。</summary>
    void Changed(ULONGLONG now){if(Target.Window&&!Dirty){Dirty=true;Due=now+250;}}
    /// <summary>最多一个在途请求由外层拥有；本方法只取走就绪的最新需求。</summary>
    std::optional<PreviewTicket> Take(ULONGLONG now){
        if(!Target.Window||now<After||now<Due)return {};if(!Dirty&&(!Known||!Due))return {};
        Dirty=false;Due=0;return PreviewTicket{Target,Generation,now+250};
    }
    /// <summary>超时退避5/30秒；不清除已核验的同代视口需求。</summary>
    void Failed(ULONGLONG now,HRESULT code,const std::string& stage){
        LastError=code;LastStage=stage;if(code==HRESULT_FROM_WIN32(ERROR_TIMEOUT))++Timeouts;
        After=now+(++Failures>=2?30000:5000);Due=After;Dirty=true;Value["complete"]=false;
    }
    /// <summary>身份或代次变化、过期均丢弃；成功普通目录三十秒兜底，挂载五秒补查。</summary>
    bool Complete(const PreviewTicket& ticket,Json result,ULONGLONG now){
        if(ticket.Target!=Target||ticket.Generation!=Generation){++Late;return false;}
        if(result.value("finishedTick",now)>ticket.Deadline){++Late;Failed(now,HRESULT_FROM_WIN32(ERROR_TIMEOUT),"late-result");return false;}
        ++Samples;DirectoryMs+=result.value("directoryMs",0ULL);SampleMs+=result.value("sampleMs",0ULL);
        if(!result.value("complete",false)){Failed(now,result.value("hresult",E_FAIL),result.value("stage",std::string("viewport")));return true;}
        Known=true;Failures=0;After=0;LastError=S_OK;LastStage.clear();Value=std::move(result);
        if(!Value.value("owned",false))++Filtered;if(!Dirty)Due=now+(Value.value("owned",false)?5000:30000);return true;
    }
    /// <summary>有界内部快照；身份数值仅在拥有的匿名管道传递，不进入公共IPC。</summary>
    Json Frame()const{
        Json views=Json::array();if(Value.value("owned",false))views.push_back(Value);
        return {{"kind","previewViews"},{"foreground",reinterpret_cast<ULONG_PTR>(Target.Window)},{"foregroundProcess",Target.Process},{"generation",Generation},{"views",views},
            {"diagnostics",{{"strategy","UIA.viewport"},{"scope","foreground"},{"windows",Target.Window?1:0},{"accepted",views.size()},{"samples",Samples},{"timeouts",Timeouts},{"late",Late},{"filtered",Filtered},{"directoryMs",DirectoryMs},{"sampleMs",SampleMs},{"queued",Dirty?1:0},{"enumerationComplete",Value.value("complete",false)}}}};
    }
};
/// <summary>最多保留八个已采样标签页；控制线程独占，失焦只降级，导航/关闭删除自身需求。</summary>
class RetainedPreviewViews {
    struct Item {Json Value;ULONGLONG Used{};};std::map<std::string,Item> Items;
public:
    void Accept(const ForegroundPreviewState& state,ULONGLONG now){
        if(!state.Value.value("owned",false)||!state.Value.value("complete",false)||!state.Value.contains("viewId"))return;
        auto value=state.Value;const auto id=value.at("viewId").get<std::string>();value["frameWindow"]=reinterpret_cast<ULONG_PTR>(state.Target.Window);value["frameProcess"]=state.Target.Process;value["generation"]=state.Generation;
        Items[id]={std::move(value),now};
        while(Items.size()>8)Items.erase(std::min_element(Items.begin(),Items.end(),[](const auto& left,const auto& right){return left.second.Used<right.second.Used;}));
        auto bytes=[&]{size_t total{};for(const auto& [_,item]:Items)total+=item.Value.dump().size();return total;};
        while(Items.size()>1&&bytes()>256*1024)Items.erase(std::min_element(Items.begin(),Items.end(),[](const auto& left,const auto& right){return left.second.Used<right.second.Used;}));
    }
    bool Forget(const std::string& id){return Items.erase(id)>0;}
    bool Prune(const std::function<bool(const Json&)>& valid){bool changed=false;for(auto item=Items.begin();item!=Items.end();)if(!valid(item->second.Value)){item=Items.erase(item);changed=true;}else ++item;return changed;}
    Json Frame(const ForegroundPreviewState& state)const{
        auto frame=state.Frame();auto views=Json::array();const auto active=state.Value.value("viewId",std::string{});
        for(const auto& [id,item]:Items){auto value=item.Value;value["foreground"]=state.Target.Window&&id==active&&value.value("frameWindow",0ULL)==reinterpret_cast<ULONG_PTR>(state.Target.Window);views.push_back(std::move(value));}
        frame["views"]=std::move(views);frame["diagnostics"]["enumerationComplete"]=true;frame["diagnostics"]["retained"]=Items.size();return frame;
    }
};
/// <summary>只处理支持的媒体提示；普通目录与不支持项目不能触发生成。</summary>
inline bool PreviewMediaPath(const std::wstring& path){
    const auto dot=path.find_last_of(L'.');if(dot==std::wstring::npos)return false;auto extension=path.substr(dot);for(auto& letter:extension)letter=static_cast<wchar_t>(towlower(letter));
    for(auto item:{L".jpg",L".jpeg",L".png",L".bmp",L".gif",L".tif",L".tiff",L".heic",L".heif",L".webp",L".avif",L".mp4",L".exe"})if(extension==item)return true;return false;
}
/// <summary>父进程的变化发布器；心跳不核验根，同需求不重复发布，撤销无需访问根。</summary>
class PreviewDemandPublisher {
    std::map<std::string,Json> Last;
public:
    ULONGLONG RootChecks{},Publications{};
    using Roots=std::map<std::string,Json>;
    using Send=std::function<void(const std::string&,const Json&,const Json&)>;
    /// <summary>完整有界视图快照按根合并；焦点变化只改优先级，不重复核验相同路径集合。</summary>
    void ProcessDetailed(const Json& frame,const Roots& roots,const std::function<void(const Json&)>& verify,const Send& send){
        if(frame.value("kind",std::string{})!="previewViews")return;Require(frame.at("views").is_array()&&frame.at("views").size()<=8);
        if(!frame.value("diagnostics",Json::object()).value("enumerationComplete",false))return;
        for(auto item=Last.begin();item!=Last.end();)if(!roots.contains(item->first))item=Last.erase(item);else ++item;
        for(const auto& [session,root]:roots)try{
            const auto base=FullPath(Text(root,"rootPath"));Json paths=Json::array(),foreground=Json::array(),views=Json::array();std::set<std::wstring> seen,focused;ULONG_PTR window{},viewWindow{};
            for(const auto& view:frame.at("views")){
                if(!view.value("complete",false))continue;auto directory=FullPath(Text(view,"directory"));
                if(!(SamePath(base,directory)||(directory.size()>base.size()&&directory[base.size()]==L'\\'&&SamePath(base,directory.substr(0,base.size())))))continue;
                Require(view.at("paths").is_array()&&view.at("paths").size()<=64);const bool active=view.value("foreground",true);Json selected=Json::array();
                for(const auto& item:view.at("paths")){
                    auto path=FullPath(Wide(item.get<std::string>()));
                    if(path.size()>4096||path.size()<=directory.size()||path[directory.size()]!=L'\\'||!SamePath(directory,path.substr(0,directory.size()))||path.find(L'\\',directory.size()+1)!=std::wstring::npos||!PreviewMediaPath(path))continue;
                    if(seen.insert(path).second)paths.push_back(Utf8(path));selected.push_back(Utf8(path));if(active&&focused.insert(path).second)foreground.push_back(Utf8(path));
                }
                views.push_back({{"id",view.value("viewId",std::string{})},{"generation",view.value("generation",0ULL)},{"foreground",active},{"paths",selected}});
                if(active){window=frame.value("foreground",static_cast<ULONG_PTR>(0));viewWindow=view.value("viewWindow",static_cast<ULONG_PTR>(0));}
            }
            Json data={{"paths",paths},{"foregroundPaths",foreground},{"views",views},{"foregroundWindow",window},{"viewWindow",viewWindow}};
            const auto previous=Last.find(session);if((previous==Last.end()&&paths.empty())||(previous!=Last.end()&&previous->second==data))continue;
            if(!paths.empty()&&(previous==Last.end()||previous->second.at("paths")!=paths)){++RootChecks;verify(root);}send(session,root,data);Last[session]=std::move(data);++Publications;
        }catch(...){/* 身份故障不接纳新需求，其他根继续；读取和发布前仍逐文件核验。 */}
    }
    void Process(const Json& frame,const Roots& roots,const std::function<void(const Json&)>& verify,const Send& send){ProcessDetailed(frame,roots,verify,[&](const std::string& session,const Json& root,const Json& data){send(session,root,data.at("paths"));});}
    void ClearDetailed(const Roots& roots,const Send& send){ProcessDetailed({{"kind","previewViews"},{"views",Json::array()},{"diagnostics",{{"enumerationComplete",true}}}},roots,[](const Json&){},send);}
    void Clear(const Roots& roots,const Send& send){ClearDetailed(roots,[&](const std::string& session,const Json& root,const Json& data){send(session,root,data.at("paths"));});}
    /// <summary>父进程的本地前台检查立即降级已有需求，心跳和失焦不触发Shell或根查询。</summary>
    void Demote(const Roots& roots,const Send& send){for(auto& [session,data]:Last){auto root=roots.find(session);if(root==roots.end()||data.at("foregroundPaths").empty())continue;data["foregroundPaths"]=Json::array();data["foregroundWindow"]=0;data["viewWindow"]=0;for(auto& view:data["views"])view["foreground"]=false;send(session,root->second,data);++Publications;}}

};
}

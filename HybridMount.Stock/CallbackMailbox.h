#pragma once
#include "NativeCommon.h"
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <list>
#include <map>
#include <mutex>

namespace hm {
/// <summary>回调专用有界邮箱。活动为绝对计数快照；同条目的关键事件阻断跨事件合并，保持因果顺序。</summary>
class CallbackMailbox {
    struct Item {Json Value;size_t Bytes;std::string Key;bool State;};
    std::timed_mutex _gate;std::condition_variable_any _wake;std::list<Item> _items;
    std::map<std::string,std::list<Item>::iterator> _states;size_t _stateBytes{},_criticalBytes{},_critical{},_stateCount{};
    std::atomic<size_t> _count{};std::atomic<bool> _closed{};
    std::atomic<unsigned long long> _accepted{},_merged{},_rejected{},_waitMs{},_maxWaitMs{},_delivered{};
    static std::string Key(const Json& value){const auto& data=value.at("data");return value.at("sessionId").get<std::string>()+":"+(data.contains("entryId")?data.at("entryId").get<std::string>():std::string());}
    static unsigned long long Sequence(const Json& value){return std::stoull(value.at("data").at("sequence").get<std::string>());}
    void Elapsed(ULONGLONG start){auto elapsed=GetTickCount64()-start;_waitMs+=elapsed;auto high=_maxWaitMs.load();while(high<elapsed&&!_maxWaitMs.compare_exchange_weak(high,elapsed)){};}
public:
    /// <summary>截止时间由最外层回调共享；锁和容量等待共用剩余预算，失败交给回调外的宿主收尾。</summary>
    bool Push(Json value,ULONGLONG deadline)noexcept{
        const auto start=GetTickCount64();bool accepted=false;
        try{
            auto size=value.dump().size();Require(size<=256*1024);auto key=Key(value);const auto state=value.at("event")=="activity";
            const auto now=GetTickCount64();const auto remaining=deadline>now?deadline-now:0;auto end=std::chrono::steady_clock::now()+std::chrono::milliseconds(remaining);
            std::unique_lock<std::timed_mutex> lock(_gate,std::defer_lock);
            if(lock.try_lock_until(end)&&!_closed){
                auto room=[&]{auto prior=_states.find(key);return _closed||(state?(prior!=_states.end()?_stateBytes-prior->second->Bytes+size<=512*1024:_stateCount<128&&_stateBytes+size<=512*1024):_critical<256&&_criticalBytes+size<=2*1024*1024);};
                if(room()||_wake.wait_until(lock,end,room)){
                    if(!_closed){
                        auto prior=_states.find(key);
                        if(state&&prior!=_states.end()){
                            if(Sequence(value)>=Sequence(prior->second->Value)){_stateBytes=_stateBytes-prior->second->Bytes+size;prior->second->Value=std::move(value);prior->second->Bytes=size;}
                            ++_merged;accepted=true;
                        }else{
                            if(!state){if(key.ends_with(':'))_states.clear();else _states.erase(key);}
                            _items.push_back({std::move(value),size,key,state});auto item=std::prev(_items.end());if(state){_states[key]=item;++_stateCount;}else ++_critical;
                            if(state)_stateBytes+=size;else _criticalBytes+=size;++_count;++_accepted;accepted=true;
                        }
                    }
                }
            }
        }catch(...){}
        if(!accepted)++_rejected;Elapsed(start);return accepted;
    }
    /// <summary>只有独立发送线程取出；不在邮箱锁内写管道、读取文件或结束连接。</summary>
    bool Pop(Json& value){
        std::lock_guard lock(_gate);if(_items.empty())return false;auto item=_items.begin();
        if(item->State){auto found=_states.find(item->Key);if(found!=_states.end()&&found->second==item)_states.erase(found);--_stateCount;}else --_critical;
        if(item->State)_stateBytes-=item->Bytes;else _criticalBytes-=item->Bytes;value=std::move(item->Value);_items.erase(item);--_count;++_delivered;_wake.notify_all();return true;
    }
    size_t Pending()const noexcept{return _count.load();}
    void Close()noexcept{_closed=true;_wake.notify_all();}
    /// <summary>纯内存采样，不等邮箱锁、不触发任何生成或源读取。</summary>
    Json Statistics()const{return {{"pending",_count.load()},{"accepted",_accepted.load()},{"merged",_merged.load()},{"rejected",_rejected.load()},{"delivered",_delivered.load()},{"waitMs",_waitMs.load()},{"maxWaitMs",_maxWaitMs.load()}};}
};
}

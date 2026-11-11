using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Services.Localization;
namespace HybridMount.ViewModels;

/// <summary>业务反馈的明确严重程度，不按消息文案推断颜色。</summary>
public enum PageMessageSeverity { Information,Success,Warning,Error }

/// <summary>页面独立消息的展示状态；仅由UI线程更新，关闭不清空正文或取消业务。</summary>
public sealed class PageStatusMessage : ObservableObject,IDisposable
{
    private string _text="";private ResourceMessage _message=ResourceMessage.Raw("");
    private readonly IDisposable _language;
    public PageStatusMessage()=>_language=UiText.Observe(this,static value=>value.RefreshLanguage());
    private void RefreshLanguage(){if(!_disposed)SetProperty(ref _text,_message.Render(),nameof(Text));}
    private PageMessageSeverity _severity;
    public PageMessageSeverity Severity=>_severity;
    private bool _isOpen,_seenMessage,_disposed;
    /// <summary>最后一次消息正文；关闭后保留，空文字自动折叠。</summary>
    public string Text=>_text;
    /// <summary>InfoBar双向绑定的开关；释放后拒绝迟到更新。</summary>
    public bool IsOpen {get=>_isOpen;set{if(!_disposed)SetProperty(ref _isOpen,value&&_text.Length>0);}}
    /// <summary>发布一次操作反馈；即使文案相同也重新打开，返回正文是否改变。</summary>
    public bool Show(string text,PageMessageSeverity severity=PageMessageSeverity.Information)=>Publish(text,true,severity);
    /// <summary>静默更新状态；首次非空消息显示，关闭后的刷新不重新打开。</summary>
    public bool Update(string text,PageMessageSeverity severity=PageMessageSeverity.Information)=>Publish(text,false,severity);
    public bool Show(ResourceMessage message,PageMessageSeverity severity=PageMessageSeverity.Information)=>Publish(message,true,severity);
    public bool Update(ResourceMessage message,PageMessageSeverity severity=PageMessageSeverity.Information)=>Publish(message,false,severity);
    private bool Publish(string text,bool reopen,PageMessageSeverity severity)=>Publish(UiText.Describe(text),reopen,severity);
    private bool Publish(ResourceMessage message,bool reopen,PageMessageSeverity severity)
    {
        if(_disposed)return false;
        SetProperty(ref _severity,severity,nameof(Severity));
        _message=message;var changed=SetProperty(ref _text,message.Render(),nameof(Text));
        if(_text.Length==0)IsOpen=false;
        else{var first=!_seenMessage;_seenMessage=true;if(first||reopen)IsOpen=true;}
        return changed;
    }
    /// <summary>停止页面通知，不改变最后的正文或业务状态。</summary>
    public void Dispose(){if(_disposed)return;_disposed=true;_language.Dispose();}
}

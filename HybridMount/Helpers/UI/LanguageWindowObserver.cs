using System.Runtime.InteropServices;
using HybridMount.Services.Localization;
using Microsoft.UI.Xaml;
namespace HybridMount.Helpers.UI;

/// <summary>监听真实Windows设置通知并在窗口激活时复核系统语言；不重建页面，关闭前先注销原生回调。</summary>
internal sealed class LanguageWindowObserver:IDisposable
{
    private readonly Window _window;private readonly UiLanguageService _service;private readonly nint _handle;private readonly Subclass _callback;private int _disposed;
    private const nuint Id=0x484D0068;
    internal LanguageWindowObserver(Window window,UiLanguageService service)
    {
        _window=window;_service=service;_handle=WinRT.Interop.WindowNative.GetWindowHandle(window);_callback=Message;
        if(!SetWindowSubclass(_handle,_callback,Id,0))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());window.Activated+=Activated;
    }
    private void Activated(object sender,WindowActivatedEventArgs args){if(_disposed==0&&args.WindowActivationState!=WindowActivationState.Deactivated)_service.ReevaluateSystemLanguage();}
    private nint Message(nint window,uint message,nint wParam,nint lParam,nuint id,nuint data)
    {
        if(message==0x001a&&_disposed==0)_window.DispatcherQueue.TryEnqueue(()=>{if(_disposed==0)_service.ReevaluateSystemLanguage();});if(message==0x0082)Dispose();return DefSubclassProc(window,message,wParam,lParam);
    }
    public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;_window.Activated-=Activated;RemoveWindowSubclass(_handle,_callback,Id);}
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate nint Subclass(nint window,uint message,nint wParam,nint lParam,nuint id,nuint data);
    [DllImport("comctl32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetWindowSubclass(nint window,Subclass callback,nuint id,nuint data);
    [DllImport("comctl32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool RemoveWindowSubclass(nint window,Subclass callback,nuint id);
    [DllImport("comctl32.dll")]private static extern nint DefSubclassProc(nint window,uint message,nint wParam,nint lParam);
}

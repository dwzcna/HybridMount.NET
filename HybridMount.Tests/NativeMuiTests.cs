using System.Runtime.InteropServices;
using System.Text;
namespace HybridMount.Tests;

/// <summary>仅加载测试输出中的数据资源，验证标准Win32 LN／MUI与专用线程语言回退，不改系统偏好。</summary>
[Collection("Native IPC")]
public sealed class NativeMuiTests
{
    private const string Module="HybridMount.Ext.resources.dll";
    [Fact]
    public void SatellitesHaveMatchingMuiMetadataAndChineseUltimateFallback()
    {
        var neutral=Info(Path.Combine(AppContext.BaseDirectory,Module),2);var offset=(int)BitConverter.ToUInt32(neutral,44);
        Assert.Equal("zh-CN",Encoding.Unicode.GetString(neutral,offset,neutral.Length-offset).Split('\0')[0]);
        foreach(var language in new[]{"zh-CN","en-US","zh-HK"})
        {
            var mui=Info(Path.Combine(AppContext.BaseDirectory,language,Module+".mui"),4);
            Assert.Equal(neutral.Skip(12).Take(16),mui.Skip(12).Take(16));
        }
    }
    [Theory]
    [InlineData("zh-CN","释放空间")][InlineData("en-US","Free up space")][InlineData("zh-HK","釋放空間")]
    public void StandardLoaderChoosesThreadPreferredLanguage(string language,string expected)=>Assert.Equal(expected,Read(Path.Combine(AppContext.BaseDirectory,Module),language));
    [Fact]
    public void MissingRequestedSatelliteAndUnsupportedLanguageFallBackToNeutralMetadata()
    {
        var directory=Path.Combine(Path.GetTempPath(),"HybridMount.MuiTests."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(directory,"zh-CN"));
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory,Module),Path.Combine(directory,Module));File.Copy(Path.Combine(AppContext.BaseDirectory,"zh-CN",Module+".mui"),Path.Combine(directory,"zh-CN",Module+".mui"));
            Assert.Equal("释放空间",Read(Path.Combine(directory,Module),"en-US"));Assert.Equal("释放空间",Read(Path.Combine(directory,Module),"ja-JP"));
        }
        finally{Directory.Delete(directory,true);}
    }
    /// <summary>新建专用线程选择优先语言并读取LN字符串，线程退出即释放语言设置；失败交回测试线程。</summary>
    private static string Read(string path,string language)
    {
        string? result=null;Exception? failure=null;
        var thread=new Thread(()=>
        {
            nint module=0;
            try
            {
                Assert.True(SetThreadPreferredUILanguages(8,language+"\0zh-CN\0\0",out _));
                module=LoadLibraryEx(path,0,0x22);Assert.NotEqual(0,module);var text=new StringBuilder(512);Assert.NotEqual(0,LoadString(module,1002,text,text.Capacity));result=text.ToString();
            }
            catch(Exception error){failure=error;}
            finally{if(module!=0)FreeLibrary(module);}
        });thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(10)));if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();return result!;
    }
    private static byte[] Info(string path,uint type)
    {var data=new byte[4096];BitConverter.GetBytes((uint)data.Length).CopyTo(data,0);BitConverter.GetBytes((uint)1).CopyTo(data,4);uint size=(uint)data.Length;Assert.True(GetFileMUIInfo(7,path,data,ref size));Assert.Equal(type,BitConverter.ToUInt32(data,8));return data;}
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetFileMUIInfo(uint flags,string path,[In,Out]byte[] info,ref uint size);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetThreadPreferredUILanguages(uint flags,string languages,out uint count);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint LoadLibraryEx(string path,nint file,uint flags);
    [DllImport("user32",CharSet=CharSet.Unicode)]private static extern int LoadString(nint module,uint id,StringBuilder value,int maximum);
    [DllImport("kernel32")]private static extern bool FreeLibrary(nint module);
}

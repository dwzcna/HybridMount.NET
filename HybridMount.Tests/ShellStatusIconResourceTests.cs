using System.Runtime.InteropServices;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>验证真正的ICO帧和DLL资源表；加载只作资源映射，不运行扩展或创建COM对象。</summary>
public sealed class ShellStatusIconResourceTests
{
    [Theory]
    [InlineData("SyncExcluded.ico",102)]
    [InlineData("SyncIssue.ico",103)]
    public void MultiFrameIconsAreExtractableFromNativeResource(string name,int id)
    {
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets",name));
        Assert.Equal(0,BitConverter.ToUInt16(bytes,0));Assert.Equal(1,BitConverter.ToUInt16(bytes,2));
        var count=BitConverter.ToUInt16(bytes,4);Assert.Equal(9,count);var sizes=new HashSet<int>();
        for(var i=0;i<count;i++)
        {
            var pos=6+16*i;var size=bytes[pos]==0?256:bytes[pos];sizes.Add(size);
            Assert.Equal(bytes[pos],bytes[pos+1]);Assert.Equal(32,BitConverter.ToUInt16(bytes,pos+6));
            var length=BitConverter.ToInt32(bytes,pos+8);var offset=BitConverter.ToInt32(bytes,pos+12);
            Assert.True(offset>=6+16*count&&length>0&&offset<=bytes.Length-length);
            Assert.Equal(new byte[]{137,80,78,71,13,10,26,10},bytes[offset..(offset+8)]);
        }
        Assert.True(sizes.SetEquals(new[]{16,20,24,32,40,48,64,128,256}));
        var dll=Path.Combine(AppContext.BaseDirectory,"HybridMount.Ext.dll");Assert.True(File.Exists(dll),dll);
        var module=LoadLibraryEx(dll,0,0x22);Assert.NotEqual(0,module);
        try
        {
            foreach(var size in new[]{16,32,64,256})
            {
                var icon=LoadImage(module,id,1,size,size,0);Assert.NotEqual(0,icon);
                try
                {
                    Assert.True(GetIconInfo(icon,out var info));
                    try{Assert.NotEqual(0,info.Color);Assert.NotEqual(0,GetObject(info.Color,Marshal.SizeOf<Bitmap>(),out var bitmap));Assert.Equal(size,bitmap.Width);Assert.Equal(size,bitmap.Height);Assert.Equal(32,bitmap.BitsPixel);}
                    finally{if(info.Color!=0)DeleteObject(info.Color);if(info.Mask!=0)DeleteObject(info.Mask);}
                }
                finally{DestroyIcon(icon);}
            }
        }
        finally{FreeLibrary(module);}
        Assert.Equal(dll+",-"+id,ShellStatusIconResources.Reference(id));
    }
    [StructLayout(LayoutKind.Sequential)]private struct IconInfo{public int Icon;public uint X,Y;public nint Mask,Color;}
    [StructLayout(LayoutKind.Sequential)]private struct Bitmap{public int Type,Width,Height,WidthBytes;public ushort Planes,BitsPixel;public nint Bits;}
    [DllImport("kernel32.dll",EntryPoint="LoadLibraryExW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint LoadLibraryEx(string path,nint file,uint flags);
    [DllImport("kernel32.dll")]private static extern bool FreeLibrary(nint module);
    [DllImport("user32.dll",EntryPoint="LoadImageW",SetLastError=true)]private static extern nint LoadImage(nint module,nint resource,uint type,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool GetIconInfo(nint icon,out IconInfo info);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(nint icon);
    [DllImport("gdi32.dll")]private static extern int GetObject(nint bitmap,int size,out Bitmap info);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint obj);
}

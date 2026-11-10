using System.Reflection.PortableExecutable;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace HybridMount.Models.Thumbnails;

/// <summary>将已验证为完整本地内容的 PE 当作只读资源加载；不执行入口点、依赖项或目标代码。</summary>
internal static class ExeIconExtractor
{
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    { public uint Size;public int Width,Height;public ushort Planes,BitCount;public uint Compression,ImageSize;public int XPels,YPels;public uint Colors,Important; }
    [DllImport("user32.dll")] private static extern nint CreateIconFromResourceEx(byte[] bytes,uint length,[MarshalAs(UnmanagedType.Bool)]bool icon,uint version,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc,int x,int y,nint icon,int width,int height,uint step,nint brush,uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc,ref BitmapHeader info,uint usage,out nint bits,nint section,uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc,nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);

    /// <summary>调用方必须持有防脱水 oplock 并确认文件完整；资源错误返回空值，不回退到 Shell 文件打开。</summary>
    public static byte[]? Extract(Stream content,int size)
    {
        if(size is <16 or >256)throw new ArgumentOutOfRangeException(nameof(size));
        content.Position=0;
        try
        {
            using var pe=new PEReader(content,PEStreamOptions.LeaveOpen);
            var directory=pe.PEHeaders.PEHeader?.ResourceTableDirectory;
            if(directory is null||directory.Value.Size is <16 or >16777216)return null;
            var rootRva=directory.Value.RelativeVirtualAddress;
            var resources=pe.GetSectionData(rootRva).GetContent(0,directory.Value.Size).ToArray();
            var bytes=Resource(resources,rootRva,14,null);
            if(bytes is null||bytes.Length<6||BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2))!=1)return null;
            var count=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));if(count==0||count>256||bytes.Length<6+count*14)return null;
            var selected=0;var best=int.MaxValue;
            for(var i=0;i<count;i++)
            {
                var offset=6+i*14;var width=bytes[offset]==0?256:bytes[offset];
                var score=width>=size?width-size:1000+size-width;
                if(score<best){selected=offset;best=score;}
            }
            var id=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(selected+12));
            var iconBytes=Resource(resources,rootRva,3,id);if(iconBytes is null)return null;

            var icon=CreateIconFromResourceEx(iconBytes,(uint)iconBytes.Length,true,0x30000,size,size,0);if(icon==0)return null;
            try
            {
                var black=Render(icon,size,0);var white=Render(icon,size,255);if(black is null||white is null)return null;
                var rgba=new byte[black.Length];
                for(var i=0;i<black.Length;i+=4)
                {
                    var alpha=255-Math.Clamp(Math.Max(white[i]-black[i],Math.Max(white[i+1]-black[i+1],white[i+2]-black[i+2])),0,255);
                    rgba[i]=(byte)(alpha==0?0:Math.Min(255,black[i+2]*255/alpha));
                    rgba[i+1]=(byte)(alpha==0?0:Math.Min(255,black[i+1]*255/alpha));
                    rgba[i+2]=(byte)(alpha==0?0:Math.Min(255,black[i]*255/alpha));rgba[i+3]=(byte)alpha;
                }
                return Png(rgba,size);
            }
            finally{DestroyIcon(icon);}
        }
        catch(BadImageFormatException){return null;}
        catch(ArgumentException){return null;}
    }
    /// <summary>仅解析有界资源目录，拒绝跨段 RVA、越界和错误的目录层级。</summary>
    private static byte[]? Resource(byte[] bytes,int rootRva,int type,int? name)
    {
        int? Child(int directory,int? identity,bool subdirectory)
        {
            if(directory<0||directory>bytes.Length-16)return null;
            var count=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(directory+12))+BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(directory+14));
            if(count>4096||directory+16L+count*8L>bytes.Length)return null;
            for(var index=0;index<count;index++)
            {
                var position=directory+16+index*8;var id=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position));
                if(identity is not null&&id!=(uint)identity.Value)continue;
                var value=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position+4));
                if(((value&0x80000000)!=0)!=subdirectory)return null;
                return (int)(value&0x7fffffff);
            }
            return null;
        }
        var first=Child(0,type,true);if(first is null)return null;
        var second=Child(first.Value,name,true);if(second is null)return null;
        var data=Child(second.Value,null,false);if(data is null||data<0||data>bytes.Length-16)return null;
        var rva=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(data.Value));var size=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(data.Value+4));
        var offset=(long)rva-rootRva;if(size is 0 or >4194304||offset<0||offset+size>bytes.Length)return null;
        return bytes.AsSpan((int)offset,(int)size).ToArray();
    }

    private static byte[]? Render(nint icon,int size,byte background)
    {
        var dc=CreateCompatibleDC(0);if(dc==0)return null;
        nint bitmap=0,previous=0;
        try
        {
            var header=new BitmapHeader{Size=40,Width=size,Height=-size,Planes=1,BitCount=32};
            bitmap=CreateDIBSection(dc,ref header,0,out var bits,0,0);if(bitmap==0||bits==0)return null;
            previous=SelectObject(dc,bitmap);var pixels=Enumerable.Repeat(background,size*size*4).ToArray();Marshal.Copy(pixels,0,bits,pixels.Length);
            if(!DrawIconEx(dc,0,0,icon,size,size,0,0,3))return null;Marshal.Copy(bits,pixels,0,pixels.Length);return pixels;
        }
        finally{if(previous!=0)SelectObject(dc,previous);if(bitmap!=0)DeleteObject(bitmap);DeleteDC(dc);}
    }
    private static byte[] Png(byte[] rgba,int size)
    {
        using var output=new MemoryStream();output.Write(new byte[]{137,80,78,71,13,10,26,10});
        var header=new byte[13];BinaryPrimitives.WriteInt32BigEndian(header,size);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),size);header[8]=8;header[9]=6;Chunk(output,"IHDR"u8,header);
        using var compressed=new MemoryStream();
        using(var zlib=new ZLibStream(compressed,CompressionLevel.Fastest,true))for(var row=0;row<size;row++){zlib.WriteByte(0);zlib.Write(rgba,row*size*4,size*4);}
        Chunk(output,"IDAT"u8,compressed.ToArray());Chunk(output,"IEND"u8,[]);return output.ToArray();
    }
    private static void Chunk(Stream output,ReadOnlySpan<byte> type,ReadOnlySpan<byte> bytes)
    {
        Span<byte> field=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(field,bytes.Length);output.Write(field);output.Write(type);output.Write(bytes);
        var crc=uint.MaxValue;
        foreach(var value in type)crc=Crc(crc,value);foreach(var value in bytes)crc=Crc(crc,value);
        BinaryPrimitives.WriteUInt32BigEndian(field,~crc);output.Write(field);
    }
    private static uint Crc(uint crc,byte value){crc^=value;for(var bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);return crc;}
}

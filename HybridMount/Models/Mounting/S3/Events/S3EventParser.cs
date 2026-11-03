using System.Text;
using System.Text.Json;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>AWS S3 原生通知解析；整体校验通过前不接受部分记录，不保存原始消息正文。</summary>
internal static class S3EventParser
{
    internal static S3Notification Parse(string body)
    {
        if(Encoding.UTF8.GetByteCount(body)>1024*1024)throw new IOException("S3 事件超过消息预算，未确认；请检查专用队列及 DLQ。");
        try
        {
            using var document=JsonDocument.Parse(body,new(){MaxDepth=16});var root=document.RootElement;
            if(root.TryGetProperty("Event",out var test)&&test.GetString()=="s3:TestEvent")
            {if(root.GetProperty("Service").GetString()!="Amazon S3")throw new JsonException();return new(true,[]);}
            var records=root.GetProperty("Records");if(records.ValueKind!=JsonValueKind.Array||records.GetArrayLength() is <1 or >1000)throw new JsonException();
            var result=new List<S3EventRecord>();
            foreach(var record in records.EnumerateArray())
            {
                var version=record.GetProperty("eventVersion").GetString()!.Split('.');
                if(version.Length!=2||version[0]!="2"||!int.TryParse(version[1],out var minor)||minor<1||record.GetProperty("eventSource").GetString()!="aws:s3")throw new JsonException();
                var operation=Text(record,"eventName",128);if(!operation.StartsWith("ObjectCreated:",StringComparison.Ordinal)&&!operation.StartsWith("ObjectRemoved:",StringComparison.Ordinal))throw new JsonException();
                var region=Text(record,"awsRegion",64);var s3=record.GetProperty("s3");var bucket=Text(s3.GetProperty("bucket"),"name",63);var item=s3.GetProperty("object");
                var key=DecodeKey(Text(item,"key",8192));var sequence=Text(item,"sequencer",128);
                if(sequence.Any(x=>!Uri.IsHexDigit(x)))throw new JsonException();sequence=sequence.TrimStart('0').ToUpperInvariant();if(sequence.Length==0)sequence="0";
                var id=item.TryGetProperty("versionId",out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;if(id?.Length>2048)throw new JsonException();
                result.Add(new(bucket,key,sequence,operation,region,id));
            }
            return new(false,result);
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {throw new IOException("S3 事件格式、来源或编码无效，未确认；请检查专用队列及 DLQ。");}
    }
    private static string Text(JsonElement element,string name,int maximum)
    {var value=element.GetProperty(name).GetString();if(string.IsNullOrEmpty(value)||value.Length>maximum)throw new JsonException();return value;}
    /// <summary>表单式 URL 编码只解码一次；%2B 保留加号，+ 转为空格，拒绝无效百分号与 UTF-8。</summary>
    internal static string DecodeKey(string encoded)
    {
        using var bytes=new MemoryStream();
        for(var i=0;i<encoded.Length;i++)
        {
            var value=encoded[i];
            if(value=='%'){if(i+2>=encoded.Length||!byte.TryParse(encoded.AsSpan(i+1,2),System.Globalization.NumberStyles.HexNumber,null,out var b))throw new JsonException();bytes.WriteByte(b);i+=2;}
            else{if(value>127)throw new JsonException();bytes.WriteByte((byte)(value=='+'?' ':value));}
        }
        return new UTF8Encoding(false,true).GetString(bytes.ToArray());
    }
    internal static bool Newer(string incoming,string current)=>incoming.Length>current.Length||incoming.Length==current.Length&&string.CompareOrdinal(incoming,current)>0;
}

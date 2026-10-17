using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ITSeti.Server;

// Reads only the PE resource directory and bounded RT_VERSION payloads, never executes the installer.
internal static class PeVersionResource
{
    internal sealed record Metadata(string? ProductName,string? Description,string? ProductVersion,string? FileVersion);

    public static Metadata? Read(string path)
    {
        using var stream=File.OpenRead(path);
        using var pe=new PEReader(stream,PEStreamOptions.LeaveOpen);
        var directory=pe.PEHeaders.PEHeader?.ResourceTableDirectory ?? default;
        if(directory.RelativeVirtualAddress<=0||directory.Size<16)return null;
        long Map(long rva,int length)
        {
            foreach(var section in pe.PEHeaders.SectionHeaders)
            {
                var delta=rva-section.VirtualAddress;
                var offset=section.PointerToRawData+delta;
                if(delta>=0 && delta+length<=section.SizeOfRawData && offset>=0 && offset<=stream.Length-length)
                    return offset;
            }
            throw new BadImageFormatException("Resource RVA outside the PE file.");
        }
        byte[] ReadAt(long rva,int length)
        {
            if(length<0||length>65536)throw new BadImageFormatException("Version resource exceeds the limit.");
            var data=new byte[length];stream.Position=Map(rva,length);stream.ReadExactly(data);return data;
        }
        byte[] Relative(uint offset,int length)
        {
            if((long)offset+length>directory.Size)throw new BadImageFormatException("Resource directory offset outside its bounds.");
            return ReadAt((long)directory.RelativeVirtualAddress+offset,length);
        }
        var budget=4096;
        IEnumerable<(uint Name,uint Address)> Entries(uint offset)
        {
            var header=Relative(offset,16);
            var count=U16(header,12)+U16(header,14);
            if(count>4096)throw new BadImageFormatException("Excessive resource entries.");
            for(var i=0;i<count;i++)
            {
                if(--budget<0)throw new BadImageFormatException("Resource traversal exceeds the limit.");
                var entry=Relative(checked(offset+16+(uint)i*8),8);
                yield return (U32(entry,0),U32(entry,4));
            }
        }
        IEnumerable<byte[]> Payloads(uint address,int depth)
        {
            if(depth>3)throw new BadImageFormatException("Invalid version resource depth.");
            if((address&0x80000000)!=0)
            {
                foreach(var entry in Entries(address&0x7fffffff))
                    foreach(var payload in Payloads(entry.Address,depth+1))yield return payload;
            }
            else
            {
                var leaf=Relative(address,16);var size=U32(leaf,4);
                if(size is < 6 or > 65536)throw new BadImageFormatException("Invalid version resource size.");
                yield return ReadAt(U32(leaf,0),(int)size);
            }
        }
        foreach(var entry in Entries(0))
            if(entry.Name==16)
                foreach(var payload in Payloads(entry.Address,0))
                {
                    var result=Parse(payload);
                    if(result is not null)return result;
                }
        return null;
    }

    private static Metadata? Parse(byte[] data)
    {
        var strings=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string? fixedProduct=null,fixedFile=null;
        var blocks=0;
        void Block(int start,int parentEnd,int depth)
        {
            if(depth>5||++blocks>1024||start+6>parentEnd)throw new BadImageFormatException("Invalid version information tree.");
            var length=U16(data,start);var valueLength=U16(data,start+2);var type=U16(data,start+4);
            var end=start+length;
            if(length<6||end>parentEnd)throw new BadImageFormatException("Invalid version block length.");
            var keyStart=start+6;var cursor=keyStart;
            while(cursor+2<=end&&U16(data,cursor)!=0)cursor+=2;
            if(cursor+2>end)throw new BadImageFormatException("Unterminated version key.");
            var key=Encoding.Unicode.GetString(data,keyStart,cursor-keyStart);
            if(depth==0&&key!="VS_VERSION_INFO")throw new BadImageFormatException("Missing VS_VERSION_INFO.");
            var valueStart=Align(cursor+2);var valueBytes=type==1?valueLength*2:valueLength;
            if(valueStart+valueBytes>end)throw new BadImageFormatException("Version value outside block.");
            if(depth==0&&valueBytes>=52&&U32(data,valueStart)==0xfeef04bd)
            {
                string VersionAt(int offset)
                {
                    var high=U32(data,offset);var low=U32(data,offset+4);
                    return $"{high>>16}.{high&65535}.{low>>16}.{low&65535}";
                }
                fixedFile=VersionAt(valueStart+8);fixedProduct=VersionAt(valueStart+16);
            }
            if(type==1&&valueBytes>0&&key is "ProductName" or "FileDescription" or "ProductVersion" or "FileVersion")
                strings.TryAdd(key,Encoding.Unicode.GetString(data,valueStart,valueBytes).TrimEnd('\0').Trim());
            cursor=Align(valueStart+valueBytes);
            while(cursor+6<=end)
            {
                var childLength=U16(data,cursor);if(childLength==0)break;
                Block(cursor,end,depth+1);cursor=Align(cursor+childLength);
            }
        }
        if(data.Length<6)return null;
        Block(0,data.Length,0);
        string? Value(string key)=>strings.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)?value:null;
        return new(Value("ProductName"),Value("FileDescription"),Value("ProductVersion")??fixedProduct,Value("FileVersion")??fixedFile);
    }
    private static int Align(int value)=>(value+3)&~3;
    private static ushort U16(byte[] data,int offset)=>BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset,2));
    private static uint U32(byte[] data,int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset,4));
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using ZstdSharp;

namespace Nexus.Pc;

// A package member or one compressed block. A truncated member cannot consume
// the first bytes of the next archive entry as if they belonged to this one.
internal sealed class BoundedReader(Stream input,long length):Stream
{
    readonly long totalLength=length;
    long remaining=length;
    public long Remaining=>remaining;
    public override int Read(Span<byte> buffer){
        int read=input.Read(buffer[..(int)Math.Min(buffer.Length,remaining)]);remaining-=read;return read;
    }
    public override int Read(byte[] b,int off,int count)=>Read(b.AsSpan(off,count));
    public override bool CanRead=>true;public override bool CanWrite=>false;public override bool CanSeek=>false;
    public override long Position{get=>totalLength-remaining;set=>throw new NotSupportedException();}
    public override long Length=>totalLength;
    public override long Seek(long pos,SeekOrigin where)=>throw new NotSupportedException();
    public override void SetLength(long size)=>throw new NotSupportedException();
    public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
    public override void Flush(){}
}

internal static class Ncz
{
    const int Prefix=0x4000;
    sealed record Section(long Start,long End,long Mode,byte[] Key,byte[] Nonce);
    static void Need(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
    internal static void Decode(Stream input,Part file,Action<byte[],int> emit,CancellationToken token)
    {
        Need(file.Offset>=0 && file.Size>=Prefix && file.StoredSize>=Prefix+16 &&
            file.StoredSize<=input.Length-file.Offset,"Truncated compressed game file.");
        input.Position=file.Offset;
        using var member=new BoundedReader(input,file.StoredSize);
        using var reader=new BinaryReader(member,System.Text.Encoding.UTF8,true);
        var buffer=new byte[256*1024];
        member.ReadExactly(buffer.AsSpan(0,Prefix));emit(buffer,Prefix);
        Need(reader.ReadBytes(8).AsSpan().SequenceEqual("NCZSECTN"u8),"Invalid NCZ section header.");
        long count=reader.ReadInt64();Need(count is >0 and <=1024,"Invalid NCZ section count.");
        var sections=new List<Section>();long previous=0;
        for(int i=0;i<count;i++){
            var offset=reader.ReadInt64();var size=reader.ReadInt64();var mode=reader.ReadInt64();reader.ReadInt64();
            Need(offset>=previous && size>0 && offset<=file.Size && size<=file.Size-offset && mode is 0 or 1 or 3 or 4,"Unsupported or overlapping NCZ section.");
            var key=reader.ReadBytes(16);var nonce=reader.ReadBytes(16);Need(nonce.Length==16,"Truncated NCZ key area.");
            previous=offset+size;sections.Add(new(offset,previous,mode,key,nonce));
        }
        long output=Prefix;int index=0;
        var counters=new byte[buffer.Length+32];var mask=new byte[counters.Length];
        using var aes=Aes.Create();
        Section? keyed=null;
        void Pump(Stream from,long bytes){
            while(bytes>0){
                token.ThrowIfCancellationRequested();
                while(index<sections.Count && output>=sections[index].End)index++;
                var s=index<sections.Count?sections[index]:null;
                bool inside=s!=null && output>=s.Start;
                long boundary=s==null?file.Size:inside?s.End:s.Start;
                int n=(int)Math.Min(bytes,Math.Min(buffer.Length,boundary-output));
                Need(n>0,"NCZ output exceeds the declared content size.");
                from.ReadExactly(buffer.AsSpan(0,n));
                if(inside && s!.Mode is 3 or 4){
                    if(keyed!=s){aes.Key=s.Key;keyed=s;}
                    int skip=(int)(output%16),blocks=(skip+n+15)/16;
                    for(int i=0;i<blocks;i++){
                        s.Nonce.AsSpan(0,8).CopyTo(counters.AsSpan(i*16));
                        BinaryPrimitives.WriteUInt64BigEndian(counters.AsSpan(i*16+8),(ulong)(output/16+i));
                    }
                    aes.EncryptEcb(counters.AsSpan(0,blocks*16),mask,PaddingMode.None);
                    for(int i=0;i<n;i++)buffer[i]^=mask[skip+i];
                }
                emit(buffer,n);output+=n;bytes-=n;
            }
        }
        void Frame(Stream source,long bytes){
            using var z=new DecompressionStream(source,leaveOpen:true);
            Pump(z,bytes);Need(z.ReadByte()==-1,"NCZ frame is larger than its declared output.");
        }
        try{
            token.ThrowIfCancellationRequested();
            long body=file.Size-Prefix;
            Need(member.Remaining>=8,"Missing NCZ data stream.");
            long at=input.Position;byte[] signature=new byte[8];input.ReadExactly(signature);input.Position=at;
            if(signature.AsSpan().SequenceEqual("NCZBLOCK"u8)){
                reader.ReadUInt64();byte version=reader.ReadByte(),type=reader.ReadByte();reader.ReadByte();byte power=reader.ReadByte();
                uint blocks=reader.ReadUInt32();long length=reader.ReadInt64();
                Need(version==2 && type==1 && power is >=14 and <=32 && length==body && blocks is >0 and <=4194304,"Invalid NCZ block table.");
                long blockSize=1L<<power;Need(blocks==(body-1)/blockSize+1 && blocks*4L<=member.Remaining,"Wrong NCZ block count.");
                var sizes=new uint[blocks];long total=0;
                for(int i=0;i<sizes.Length;i++){sizes[i]=reader.ReadUInt32();total=checked(total+sizes[i]);}
                Need(total==member.Remaining,"NCZ block sizes do not match the package.");
                foreach(uint stored in sizes){
                    long size=Math.Min(blockSize,file.Size-output);
                    Need(stored>0 && stored<=size,"Invalid NCZ block length.");
                    using var block=new BoundedReader(member,stored);
                    if(stored==size)Pump(block,size);else Frame(block,size);
                    Need(block.Remaining==0,"Unused bytes in NCZ block.");
                }
            }else Frame(member,body);
            Need(output==file.Size && member.Remaining==0,"Incomplete compressed content.");
        }finally{
            foreach(var s in sections)CryptographicOperations.ZeroMemory(s.Key);
        }
    }
}

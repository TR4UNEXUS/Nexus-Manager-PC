using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;

namespace Nexus.Pc;

public sealed record ControlInfo(string? Name,string? DisplayVersion,byte[]? Icon)
{
    internal (ulong Title,bool Nexus)? Mark { get; init; }

    static (ulong Title,bool Nexus)? ReadCompressionMark(IFileSystem fs){
        var bytes=ReadFile(fs,"/hacbrewpacklz_copy.txt",4096);
        if(bytes==null)return null;
        var lines=Encoding.ASCII.GetString(bytes).Split('\n');
        if(lines.Length<3||lines[0]!="hacBrewPackLZ copy 1"||!lines[1].StartsWith("made by ",StringComparison.Ordinal)||
            lines[1].Length<=8||!lines[2].StartsWith("original ",StringComparison.Ordinal)||
            !long.TryParse(lines[2].AsSpan(9),System.Globalization.NumberStyles.None,null,out _))return null;
        if(lines.Length<4||lines[3].Length==0)return (0,false); // Legacy mark without a title ID.
        var fields=lines[3].Split(' ');
        if(fields.Length!=6||fields[0]!="for"||fields[2]!="level"||fields[4]!="block"||fields[1].Length!=16||
            !ulong.TryParse(fields[1],System.Globalization.NumberStyles.AllowHexSpecifier,null,out var title))return null;
        return (title,lines.Length>4&&lines[4]=="created by Nexus Manager for PC");
    }

    static readonly string[] Languages = ["AmericanEnglish","BritishEnglish","Japanese","French","German",
        "LatinAmericanSpanish","Spanish","Italian","Dutch","CanadianFrench","Portuguese","Russian",
        "Korean","TraditionalChinese","SimplifiedChinese","BrazilianPortuguese"];

    static byte[]? ReadFile(IFileSystem fs,string path,int limit)
    {
        using var file=new UniqueRef<IFile>();
        if(fs.OpenFile(ref file.Ref,new U8Span(path),OpenMode.Read).IsFailure())return null;
        using var input=file.Get.AsStream(OpenMode.Read,true);
        if(input.Length<=0 || input.Length>limit)return null;
        byte[] bytes=new byte[(int)input.Length];input.ReadExactly(bytes);return bytes;
    }

    static string? Field(ReadOnlySpan<byte> bytes)
    {
        int end=bytes.IndexOf((byte)0);
        if(end>=0)bytes=bytes[..end];
        try {
            var value=new UTF8Encoding(false,true).GetString(bytes).Trim();
            return value.Length>0 && !value.Any(char.IsControl)?value:null;
        }catch(DecoderFallbackException){return null;}
    }

    static string? ReadTitle(byte[] data,out int language)
    {
        language=-1;
        ReadOnlySpan<byte> names=data.AsSpan(0,0x3000);
        if(data[0x3215]==1){
            int size=BinaryPrimitives.ReadUInt16LittleEndian(names);
            if(size==0||size>0x2ffe)return null;
            using var stream=new MemoryStream(data,2,size,false);
            using var inflate=new DeflateStream(stream,CompressionMode.Decompress);
            byte[] expanded=new byte[0x6000];
            inflate.ReadExactly(expanded);
            names=expanded;
        }else if(data[0x3215]!=0)return null;
        for(int i=0;i<names.Length/0x300;i++){
            var title=Field(names.Slice(i*0x300,0x200));
            if(title!=null){language=i;return title;}
        }
        return null;
    }

    internal static ControlInfo? Read(Nca nca)
    {
        try {
            using var fs=nca.OpenFileSystem(NcaSectionType.Data,IntegrityCheckLevel.ErrorOnInvalid);
            string? title=null,version=null;int language=-1;
            var data=ReadFile(fs,"/control.nacp",0x10000);
            if(data?.Length>=0x3216){
                version=Field(data.AsSpan(0x3060,16));
                try{title=ReadTitle(data,out language);}catch(IOException){}catch(InvalidDataException){}
            }
            var marker=ReadCompressionMark(fs);
            var order=language>=0&&language<Languages.Length?Languages.Prepend(Languages[language]).Distinct():Languages;
            foreach(var lang in order.Append("Default")){
                var icon=ReadFile(fs,"/icon_"+lang+".dat",512*1024);
                if(icon is {Length:>4})return new(title,version,icon){Mark=marker};
            }
            return new(title,version,null){Mark=marker};
        }catch(Exception ex) when(ex is not OutOfMemoryException and not OperationCanceledException){return null;}
    }
}

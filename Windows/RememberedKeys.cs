using System.Security.Cryptography;
using LibHac.Common.Keys;

namespace Nexus.Desktop;

internal sealed class RememberedKeys(string folder)
{
    internal static readonly RememberedKeys Current=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NexusManagerPC"));
    internal string Store=>Path.Combine(folder,"keys.dpapi");

    static byte[] ReadBounded(string path)
    {
        using var file=File.OpenRead(path);
        if(file.Length is <=0 or >4198400)throw new InvalidDataException("Choose a valid prod.keys file.");
        var bytes=new byte[(int)file.Length];file.ReadExactly(bytes);return bytes;
    }
    static KeySet Parse(byte[] bytes)
    {
        using var input=new MemoryStream(bytes,false);
        var keys=KeySet.CreateDefaultKeySet();
        ExternalKeyReader.ReadMainKeys(keys,input,KeySet.CreateKeyInfoList(),null);
        if(keys.HeaderKey.IsZeros())throw new InvalidDataException("The selected file has no usable NCA header key.");
        return keys;
    }
    public KeySet Open(string path)
    {
        if(!StringComparer.OrdinalIgnoreCase.Equals(path,Store))return Nexus.Pc.Packages.OpenKeys(path);
        var bytes=ProtectedData.Unprotect(ReadBounded(Store),null,DataProtectionScope.CurrentUser);
        try{return Parse(bytes);}finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    internal byte[] ReadForCompression(string path) =>
        StringComparer.OrdinalIgnoreCase.Equals(path,Store)
            ? ProtectedData.Unprotect(ReadBounded(Store),null,DataProtectionScope.CurrentUser)
            : ReadBounded(path);
    public void Save(string path)
    {
        var bytes=ReadBounded(path);
        try{
            Parse(bytes);
            var encrypted=ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(folder);
            var temp=Path.Combine(folder,Guid.NewGuid().ToString("N")+".tmp");
            try{
                using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(encrypted);file.Flush(true);}
                File.Move(temp,Store,true);
            }finally{File.Delete(temp);}
        }finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    public string? Restore()
    {
        try{
            if(File.Exists(Store)){Open(Store);return Store;}
            var legacy=Path.Combine(folder,"key-location.txt");
            if(File.Exists(legacy)){
                var path=File.ReadAllText(legacy).Trim();
                if(File.Exists(path)){Save(path);return Store;}
            }
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or ArgumentException){}
        return null;
    }
}

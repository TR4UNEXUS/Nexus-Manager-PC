using System.Text.Json;
using Nexus.Desktop;

var current = new Version(0,1,9,0);
string Feed(string tag, string? name = null, string? url = null, string? digest = null,
    bool draft = false, bool prerelease = false, string state = "uploaded", long size = 100)
{
    name ??= DesktopUpdate.AssetName;
    return JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease,
        assets = new[]{new { id = 1, name, browser_download_url = url ?? $"https://github.com/{DesktopUpdate.Repository}/releases/download/{tag}/{name}",
            state, size, digest = digest ?? "sha256:"+new string('0',64) }} });
}
void Reject(string feed)
{
    try { DesktopUpdate.Find(feed,current); } catch(InvalidDataException){return;}
    throw new Exception("Unexpected release accepted");
}
foreach(var tag in new[]{"v0.1.8","v0.1.9","v0.1.9.0"})
    if(DesktopUpdate.Find(Feed(tag),current)!=null)throw new Exception("Spurious update");
if(DesktopUpdate.Find(Feed("v0.1.10"),current)?.Version!=new Version(0,1,10))throw new Exception("Missed newer release");
if(DesktopUpdate.Find(Feed("v1.0.0",draft:true),current)!=null || DesktopUpdate.Find(Feed("v0.2.0-beta",prerelease:true),current)!=null)
    throw new Exception("Draft or prerelease offered");
Reject(Feed("v0.1.10",name:"Switch.zip"));
Reject(Feed("v0.1.10",url:"https://example.com/"+DesktopUpdate.AssetName));
Reject(Feed("v0.1.10",digest:"sha256:"+new string('g',64)));
Reject(Feed("v0.1.10",state:"processing"));
Reject(Feed("v0.1.10",size:0));
Console.WriteLine("Passed desktop update version, repository, asset and checksum selection tests.");

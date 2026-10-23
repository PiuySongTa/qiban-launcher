using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickLauncherWinUI3;

static class UpdateChecks
{
    private sealed class CancelDuringRead : MemoryStream
    {
        private readonly CancellationTokenSource _cancel;
        public CancelDuringRead(byte[] bytes, CancellationTokenSource cancel) : base(bytes) { _cancel = cancel; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            if (Position > 0) { _cancel.Cancel(); cancel.ThrowIfCancellationRequested(); }
            return base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancel);
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => new(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host != "api.github.com") throw new Exception("Unexpected update destination");
            if (request.Headers.Authorization != null) throw new Exception("Public update request must not send credentials");
            return Task.FromResult(Reply(request));
        }
    }
    private static object Release(string tag, bool pre = false, bool draft = false, string? assetName = null) => new
    {
        tag_name = tag, prerelease = pre, draft, body = "Release notes", assets = new[] {
            new {id=7, name=assetName ?? "Qiban-"+tag.TrimStart('v')+"-Windows-x64.zip",size=6,digest="sha256:"+new string('a',64),state="uploaded"}
        }
    };
    public static async Task Run(Action<bool, string> check)
    {
        check(AppReleaseVersion.Parse("v0.10.0").CompareTo(AppReleaseVersion.Parse("0.9.0"))>0,"versions compare numerically rather than as strings");
        check(AppReleaseVersion.Parse("1.0.0").CompareTo(AppReleaseVersion.Parse("1.0.0-rc.99"))>0,"stable version follows prereleases");
        check(AppReleaseVersion.Parse("1.0.0-beta.10").CompareTo(AppReleaseVersion.Parse("1.0.0-beta.2"))>0,"numeric prerelease identifiers compare numerically");
        check(AppReleaseVersion.Parse("1.0.0+local").CompareTo(AppReleaseVersion.Parse("1.0.0+other"))==0,"build metadata does not trigger false updates");
        string releases = JsonSerializer.Serialize(new[]{Release("v0.4.0-beta",true),Release("v0.10.0"),Release("v0.9.0"),Release("v99.0.0",draft:true)});
        check(AppUpdateClient.SelectRelease(releases,"0.3.0",false)?.Tag=="v0.10.0","highest valid stable release selected regardless of API ordering; drafts ignored");
        string beta = JsonSerializer.Serialize(new[]{Release("v0.4.0-beta",true),Release("v0.3.0")});
        check(AppUpdateClient.SelectRelease(beta,"0.3.0",false)==null && AppUpdateClient.SelectRelease(beta,"0.3.0",true)?.Tag=="v0.4.0-beta","prerelease opt-in works and equal versions do not update");
        check(AppUpdateClient.SelectRelease(JsonSerializer.Serialize(new[]{Release("v0.1.0-beta",true)}),"0.3.0",true)==null,"older published beta never downgrades current version");
        check(AppUpdateClient.SelectRelease(JsonSerializer.Serialize(new[]{Release("v9.0.0",assetName:"Source-code.zip")}),"0.3.0",false)==null,"source archive not mistaken for a runnable package");
        bool invalid=false;try{AppReleaseVersion.Parse("invalid-version");}catch(FormatException){invalid=true;}
        check(invalid,"malformed local version is rejected");
        bool leadingZero=false;try{AppReleaseVersion.Parse("1.0.0-beta.01");}catch(FormatException){leadingZero=true;}
        check(leadingZero,"ambiguous numeric prerelease identifiers are rejected");
        using (var handler = new Handler { Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent(releases) } })
        using (var client = new AppUpdateClient(handler))
            check((await client.CheckAsync("0.3.0",false,CancellationToken.None))?.Tag=="v0.10.0","public HTTP update check sends no credentials and parses release metadata");
        foreach (var code in new[]{HttpStatusCode.Unauthorized,HttpStatusCode.Forbidden,HttpStatusCode.NotFound})
        {
            using var handler = new Handler { Reply = _ => new(code) };
            using var client = new AppUpdateClient(handler);
            bool handled=false;try {await client.CheckAsync("0.3.0",false,CancellationToken.None);}catch(InvalidOperationException ex){handled=ex.Message.Contains("GitHub") || ex.Message.Contains("公开");}
            check(handled,"GitHub "+(int)code+" gives actionable failure instead of reporting latest version");
        }
        string folder=Path.Combine(AppContext.BaseDirectory,"update-fixture-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path=Path.Combine(folder,"package.zip");
            byte[] payload={1,2,3,4,5,6};
            string hash=Convert.ToHexString(SHA256.HashData(payload));
            var release=new UpdateRelease("v0.4.0","notes",new(7,"Qiban-0.4.0-Windows-x64.zip",6,"sha256:"+hash),null);
            using (var client=new AppUpdateClient(new Handler { Reply=_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(payload)} }))
                await client.DownloadAsync(release,path,null,CancellationToken.None);
            check(File.ReadAllBytes(path).SequenceEqual(payload),"download is committed only after exact length and SHA256 verification");
            File.WriteAllText(path,"existing-download");
            using (var client=new AppUpdateClient(new Handler { Reply=_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(payload)} }))
            {
                bool mismatch=false;try {await client.DownloadAsync(release with {Package=release.Package with {Digest="sha256:"+new string('0',64)}},path,null,CancellationToken.None);}catch(InvalidDataException){mismatch=true;}
                check(mismatch && File.ReadAllText(path)=="existing-download","bad checksum preserves an existing destination file");
            }
            using (var client=new AppUpdateClient(new Handler { Reply=_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[9])} }))
            {
                bool tooLarge=false;try{await client.DownloadAsync(release,path,null,CancellationToken.None);}catch(InvalidDataException){tooLarge=true;}
                check(tooLarge && File.ReadAllText(path)=="existing-download","oversized download is stopped before destination overwrite");
            }
            using (var client=new AppUpdateClient(new Handler { Reply=_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[2])} }))
            {
                bool shortRead=false;try{await client.DownloadAsync(release,path,null,CancellationToken.None);}catch(InvalidDataException){shortRead=true;}
                check(shortRead && File.ReadAllText(path)=="existing-download","truncated downloads preserve previous data");
            }
            using (var cancelled=new CancellationTokenSource())
            using (var client=new AppUpdateClient(new Handler()))
            {
                cancelled.Cancel(); bool stopped=false;
                try{await client.DownloadAsync(release,path,null,cancelled.Token);}catch(OperationCanceledException){stopped=true;}
                check(stopped && File.ReadAllText(path)=="existing-download","cancelled update does not overwrite existing data");
            }
            using (var cancelled=new CancellationTokenSource())
            using (var client=new AppUpdateClient(new Handler {Reply=_=>new(HttpStatusCode.OK){Content=new StreamContent(new CancelDuringRead(payload,cancelled))}}))
            {
                bool stopped=false;try{await client.DownloadAsync(release,path,null,cancelled.Token);}catch(OperationCanceledException){stopped=true;}
                check(stopped && File.ReadAllText(path)=="existing-download","cancellation during streamed download preserves original file");
            }
            using (var client=new AppUpdateClient(new Handler { Reply=request=>new(HttpStatusCode.OK){Content=request.RequestUri!.AbsolutePath.EndsWith("/8")
                ? new ByteArrayContent(Encoding.UTF8.GetBytes(hash+"  "+release.Package.Name)) : new ByteArrayContent(payload)} }))
                await client.DownloadAsync(release with {Package=release.Package with {Digest=null},Checksum=new(8,release.Package.Name+".sha256",100,null)},path,null,CancellationToken.None);
            check(File.ReadAllBytes(path).SequenceEqual(payload),"separate SHA256 asset fallback is supported");
            check(!Directory.GetFiles(folder,"*.partial").Any(),"failed and cancelled downloads leave no partial files");
        }
        finally
        {
            if (!Path.GetFullPath(folder).StartsWith(Path.GetFullPath(AppContext.BaseDirectory),StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
            Directory.Delete(folder,true);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLauncherWinUI3;

public sealed class AppReleaseVersion : IComparable<AppReleaseVersion>
{
    private readonly int[] _numbers;
    private readonly string[] _pre;
    private AppReleaseVersion(int[] numbers, string[] pre) { _numbers = numbers; _pre = pre; }
    public static AppReleaseVersion Parse(string text)
    {
        var match = Regex.Match(text.Trim(), @"^[vV]?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$");
        if (!match.Success) throw new FormatException("无法识别的版本号。");
        var numbers = Enumerable.Range(1, 3).Select(i => int.Parse(match.Groups[i].Value)).ToArray();
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
        if (pre.Any(p => p.Length > 1 && p[0] == '0' && Regex.IsMatch(p, @"^\d+$"))) throw new FormatException("版本号的数字标识不能有前导零。");
        return new AppReleaseVersion(numbers, pre);
    }
    public bool Prerelease => _pre.Length > 0;
    public int CompareTo(AppReleaseVersion? other)
    {
        if (other == null) return 1;
        for (int i = 0; i < 3; i++) { int c = _numbers[i].CompareTo(other._numbers[i]); if (c != 0) return c; }
        if (_pre.Length == 0 || other._pre.Length == 0) return _pre.Length == other._pre.Length ? 0 : _pre.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            bool a = Regex.IsMatch(_pre[i], @"^\d+$"), b = Regex.IsMatch(other._pre[i], @"^\d+$");
            int c = a && b ? CompareNumeric(_pre[i], other._pre[i])
                : a != b ? (a ? -1 : 1) : string.CompareOrdinal(_pre[i], other._pre[i]);
            if (c != 0) return c;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }
    private static int CompareNumeric(string a, string b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
}

public sealed record UpdateAsset(long Id, string Name, long Size, string? Digest);
public sealed record UpdateRelease(string Tag, string Notes, UpdateAsset Package, UpdateAsset? Checksum);

public sealed class AppUpdateClient : IDisposable
{
    public const string Repository = "PiuySongTa/qiban-launcher";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const string Api = "https://api.github.com/repos/" + Repository;
    private const long MaxPackage = 1024L * 1024 * 1024;
    private readonly HttpClient _client;
    public AppUpdateClient(HttpMessageHandler? handler = null)
    {
        _client = handler == null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromMinutes(10);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Qiban-Launcher-Update/1.0");
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }
    private async Task<HttpResponseMessage> SendAsync(string url, bool asset, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(asset ? "application/octet-stream" : "application/vnd.github+json"));
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode; response.Dispose();
        throw new InvalidOperationException(status switch
        {
            HttpStatusCode.Unauthorized => "GitHub 拒绝了公开读取请求，请稍后重试或查看发布页。",
            HttpStatusCode.Forbidden => "GitHub 拒绝访问或请求过于频繁，请检查权限或稍后重试。",
            HttpStatusCode.NotFound => "无法找到公开发行版，请确认仓库已公开并已发布 Windows x64 安装包，或稍后重试。",
            _ => "GitHub 请求未完成（状态 " + (int)status + "），请稍后重试。"
        });
    }
    public async Task<UpdateRelease?> CheckAsync(string current, bool includePrereleases, CancellationToken cancel)
    {
        using var response = await SendAsync(Api + "/releases?per_page=100", false, cancel);
        using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var memory = new MemoryStream();
        await CopyBoundedAsync(stream, memory, 4 * 1024 * 1024, null, cancel);
        return SelectRelease(System.Text.Encoding.UTF8.GetString(memory.ToArray()), current, includePrereleases);
    }
    public static UpdateRelease? SelectRelease(string json, string current, bool includePrereleases)
    {
        var local = AppReleaseVersion.Parse(current);
        using var doc = JsonDocument.Parse(json);
        UpdateRelease? best = null;
        AppReleaseVersion? bestVersion = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            AppReleaseVersion version;
            try { version = AppReleaseVersion.Parse(tag); } catch (Exception ex) when (ex is FormatException || ex is OverflowException) { continue; }
            if (!includePrereleases && (version.Prerelease || release.GetProperty("prerelease").GetBoolean())) continue;
            if (version.CompareTo(local) <= 0 || (bestVersion != null && version.CompareTo(bestVersion) <= 0)) continue;
            string packageName = "Qiban-" + tag.TrimStart('v', 'V') + "-Windows-x64.zip";
            var assets = release.GetProperty("assets").EnumerateArray().Where(a => !a.TryGetProperty("state", out var state) || state.GetString() == "uploaded")
                .Select(a => new UpdateAsset(a.GetProperty("id").GetInt64(), a.GetProperty("name").GetString() ?? "", a.GetProperty("size").GetInt64(),
                    a.TryGetProperty("digest", out var digest) ? digest.GetString() : null)).ToArray();
            var package = assets.FirstOrDefault(a => a.Name == packageName && a.Id > 0 && a.Size > 0 && a.Size <= MaxPackage);
            if (package == null) continue;
            string notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            best = new UpdateRelease(tag, notes.Length > 32000 ? notes[..32000] : notes, package, assets.FirstOrDefault(a => a.Name == packageName + ".sha256" && a.Id > 0 && a.Size <= 4096));
            bestVersion = version;
        }
        return best;
    }

    public async Task DownloadAsync(UpdateRelease release, string destination, IProgress<double>? progress, CancellationToken cancel)
    {
        string expected = release.Package.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? release.Package.Digest[7..] : "";
        if (!Regex.IsMatch(expected, "^[0-9a-fA-F]{64}$"))
        {
            if (release.Checksum == null) throw new InvalidOperationException("此发行版缺少 SHA256 校验信息，请在发布页检查，暂不自动下载。");
            using var checksumResponse = await SendAsync(Api + "/releases/assets/" + release.Checksum.Id, true, cancel);
            using var checksumStream = await checksumResponse.Content.ReadAsStreamAsync(cancel);
            using var text = new MemoryStream();
            await CopyBoundedAsync(checksumStream, text, 4096, null, cancel);
            var line = System.Text.Encoding.UTF8.GetString(text.ToArray()).TrimStart('\uFEFF').Trim();
            var match = Regex.Match(line, @"^([0-9a-fA-F]{64})\s+\*?([^\r\n]+)$");
            if (!match.Success || match.Groups[2].Value != release.Package.Name) throw new InvalidOperationException("发行包校验文件格式或名称不一致，下载已停止。");
            expected = match.Groups[1].Value;
        }
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await SendAsync(Api + "/releases/assets/" + release.Package.Id, true, cancel);
            using var input = await response.Content.ReadAsStreamAsync(cancel);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await CopyBoundedAsync(input, output, release.Package.Size, new Progress<long>(bytes => progress?.Report(bytes * 100d / release.Package.Size)), cancel);
            if (new FileInfo(temporary).Length != release.Package.Size) throw new InvalidDataException("下载长度不完整，请重试。");
            using (var file = File.OpenRead(temporary))
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancel));
                if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA256 校验失败，旧程序与已有下载文件均保留，请重试。");
            }
            cancel.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, IProgress<long>? progress, CancellationToken cancel)
    {
        byte[] buffer = new byte[81920]; long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancel)) != 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("服务器返回的数据超出预期大小，操作已停止。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            progress?.Report(total);
        }
    }
    public void Dispose() => _client.Dispose();
}

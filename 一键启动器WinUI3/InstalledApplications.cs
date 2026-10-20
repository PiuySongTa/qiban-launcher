using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLauncherWinUI3;

public static class InstalledApplications
{
    public const string Prefix = "shell:AppsFolder\\";
    public const string SourceRoot = "installed-apps:";
    public static bool IsEntry(string path) => path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    private static Task<T> OnSta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
    public static Task<List<ScanHit>> ReadAsync() => OnSta(() =>
    {
        var result = new List<ScanHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace("shell:::{4234d49b-0245-4df3-b780-3893943456e1}");
            if (folder == null) throw new InvalidOperationException("Windows 应用列表不可用。");
            items = ((dynamic)folder).Items();
            int count = ((dynamic)items).Count;
            for (int i = 0; i < count; i++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    string name = ((dynamic)item!).Name;
                    string id = Convert.ToString(((dynamic)item).ExtendedProperty("System.AppUserModel.ID")) ?? "";
                    // Some classic entries expose an EXE path instead of a registered identity; ordinary file scanning handles these.
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || Path.IsPathRooted(id) || id.Contains('\\') || id.Contains('/') || !seen.Add(id)) continue;
                    result.Add(new ScanHit { Name = name, Path = Prefix + id, Source = "已安装应用" });
                }
                catch (Exception ex) { AppLog.Write("Skipped Windows application entry", ex); }
                finally { Release(item); }
            }
        }
        finally { Release(items); Release(folder); Release(shell); }
        AppLog.Write("Windows application list loaded: " + result.Count);
        return result;
    });
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    // Explorer resolves registered desktop and packaged application identities.
    public static ProcessStartInfo StartInfo(string path) => new()
    {
        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
        Arguments = "\"" + path + "\"", UseShellExecute = true
    };

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageFactory { [PreserveSig] int GetImage(Size size, uint flags, out IntPtr bitmap); }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string name, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IImageFactory factory);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    public static Task<string?> GetIconAsync(string path) => OnSta<string?>(() =>
    {
        IImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickLauncherWinUI3", "registered-icons");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".png");
            if (File.Exists(file)) return file;
            Guid iid = typeof(IImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            Marshal.ThrowExceptionForHR(factory.GetImage(new Size { Width = 64, Height = 64 }, 0x4, out bitmap));
            using var image = System.Drawing.Image.FromHbitmap(bitmap);
            image.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            return file;
        }
        catch (Exception ex) { AppLog.Write("Registered application icon unavailable", ex); return null; }
        finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); Release(factory); }
    });
}

using System;
namespace QuickLauncherWinUI3;
internal static class AppLog { public static void Write(string message, Exception? ex = null) { if (ex != null) Console.WriteLine("Diagnostic: " + message + " " + ex.Message); } }

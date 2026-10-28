using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using FileAction.App.Native;

namespace FileAction.App.Shell;

/// <summary>
/// Reads what is selected in the active File Explorer window, or else what is on the clipboard
/// (copied files, or an image such as a Win+Shift+S screenshot, which is saved as PNG first).
/// </summary>
internal static class ExplorerSelection
{
    public static IReadOnlyList<string> Get(IntPtr foreground)
    {
        var fromExplorer = FromExplorer(foreground);
        return fromExplorer.Count > 0 ? fromExplorer : FromClipboard();
    }

    public static IReadOnlyList<string> FromClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
                return Clipboard.GetFileDropList().Cast<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                var name = Core.Loc.T("Snímek obrazovky", "Screenshot") + $" {DateTime.Now:yyyy-MM-dd HHmmss}";
                var target = Core.OutputPaths.Unique(KnownFolders.Incoming, name, "png");
                Wheel.DropContent.SavePng(image, target);
                return [target];
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or IOException)
        {
            // Clipboard busy or unreadable.
        }
        return [];
    }

    private static List<string> FromExplorer(IntPtr foreground)
    {
        var result = new List<string>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
            return result;
        object? shell = null;
        // Windows 11 tabs share the frame HWND; the first ShellTabWindowClass child is the visible tab.
        var activeTab = FindWindowEx(foreground, IntPtr.Zero, "ShellTabWindowClass", null);
        try
        {
            shell = Activator.CreateInstance(shellType);
            dynamic windows = ((dynamic)shell!).Windows();
            var count = (int)windows.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window is null)
                    continue;
                try
                {
                    if (new IntPtr((long)window.HWND) != foreground)
                        continue;
                    dynamic document = window.Document;
                    var items = document.SelectedItems();
                    var paths = new List<string>();
                    foreach (dynamic item in items)
                        paths.Add((string)item.Path);
                    if (paths.Count > 0)
                    {
                        result = paths;
                        if (activeTab == IntPtr.Zero || TabHandle((object)window) == activeTab)
                            break;
                    }
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
                {
                    // Not a file-system window (e.g. Control Panel).
                }
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }
        return result.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
    }

    private static IntPtr TabHandle(object window)
    {
        try
        {
            var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
            var iid = typeof(IShellBrowser).GUID;
            if (window is IServiceProvider provider
                && provider.QueryService(ref sid, ref iid, out var browserObject) == 0
                && browserObject is IShellBrowser browser
                && browser.GetWindow(out var hwnd) == 0)
                return hwnd;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    /// <summary>Only the first IOleWindow method is needed.</summary>
    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr phwnd);
    }

    public static Win32.POINT CursorOrCenter()
    {
        if (Win32.GetCursorPos(out var p))
            return p;
        return new Win32.POINT { X = 800, Y = 500 };
    }
}

using System.Diagnostics;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using FileAction.App.Shell;
using FileAction.App.UI;
using FileAction.Core;
using Microsoft.VisualBasic.FileIO;
using static FileAction.Core.Loc;

namespace FileAction.App.Actions;

/// <summary>The Actions wheel: copy, move, copy path, terminal, floating shelf and share.</summary>
internal static class FileActions
{
    public static IReadOnlyList<Operation> Build(AppSettings settings, Action save) =>
    [
        new AppAction("action.copy", T("Kopírovat", "Copy"), Glyphs.Copy, CopyFilesAsync, T("do schránky", "to clipboard")),
        new AppAction("action.move", T("Přesunout", "Move"), Glyphs.Move, items => CopyOrMoveAsync(items, move: true, settings, save), T("do složky…", "to folder…")),
        new AppAction("action.path", T("Kopírovat cestu", "Copy path"), Glyphs.Link, CopyPathAsync),
        new AppAction("action.terminal", T("Terminál", "Terminal"), Glyphs.Terminal, OpenTerminalAsync, T("otevřít zde", "open here")),
        new AppAction("action.float", T("Plovoucí panel", "Float"), Glyphs.Pin, items =>
        {
            ShelfWindow.Open(items);
            return Task.CompletedTask;
        }, T("odložit na později", "keep for later")),
        new AppAction("action.share", T("Sdílet", "Share"), Glyphs.Share, ShareHelper.ShareAsync, "Windows",
            items => items.Count > 0 && items.All(i => !i.IsDirectory)),
        new AppAction("action.copyto", T("Kopírovat do…", "Copy to…"), Glyphs.Copy,
            items => CopyOrMoveAsync(items, move: false, settings, save), T("do složky…", "to folder…")),
    ];

    private static async Task CopyFilesAsync(IReadOnlyList<FileItem> items)
    {
        var paths = new StringCollection();
        paths.AddRange(items.Select(item => Path.GetFullPath(item.Path)).ToArray());
        var data = new DataObject();
        data.SetFileDropList(paths);
        // Explorer reads this shell format to distinguish Copy (1) from Cut/Move (2).
        using var effect = new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy));
        data.SetData("Preferred DropEffect", effect);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                effect.Position = 0;
                Clipboard.SetDataObject(data, copy: true);
                break;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                await Task.Delay(50);
            }
        }
        Notifications.Show(T("Soubory zkopírovány", "Files copied"), items.Count == 1 ? items[0].Name : Items(items.Count));
    }

    private static async Task CopyOrMoveAsync(IReadOnlyList<FileItem> items, bool move, AppSettings settings, Action save)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = move ? T("Kam přesunout?", "Move to…") : T("Kam zkopírovat?", "Copy to…"),
            InitialDirectory = (move ? settings.LastMoveFolder : settings.LastCopyFolder)
                ?? Path.GetDirectoryName(items[0].Path.TrimEnd('\\', '/')) ?? "",
        };
        // We are a background app at this point; a foreground owner keeps the dialog from opening behind Explorer.
        var owner = ForegroundHelper.Host();
        try
        {
            if (dialog.ShowDialog(owner) != true)
                return;
        }
        finally
        {
            owner.Close();
        }
        var target = dialog.FolderName;
        if (move)
            settings.LastMoveFolder = target;
        else
            settings.LastCopyFolder = target;
        save();

        // The shell dialogs give the familiar progress window, conflict prompts and undo.
        await RunOnStaThread(() =>
        {
            foreach (var item in items)
            {
                var source = item.Path.TrimEnd('\\', '/');
                var destination = Path.Combine(target, item.Name);
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (item.IsDirectory)
                {
                    if (move)
                        FileSystem.MoveDirectory(source, destination, UIOption.AllDialogs, UICancelOption.DoNothing);
                    else
                        FileSystem.CopyDirectory(source, destination, UIOption.AllDialogs, UICancelOption.DoNothing);
                }
                else if (move)
                {
                    FileSystem.MoveFile(source, destination, UIOption.AllDialogs, UICancelOption.DoNothing);
                }
                else
                {
                    FileSystem.CopyFile(source, destination, UIOption.AllDialogs, UICancelOption.DoNothing);
                }
            }
        });
    }

    private static Task RunOnStaThread(Action action)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static Task CopyPathAsync(IReadOnlyList<FileItem> items)
    {
        var text = string.Join(Environment.NewLine, items.Select(i => i.Path));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                break;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                // Another app holds the clipboard open; try again shortly.
                Thread.Sleep(50);
            }
        }
        Notifications.Show(T("Cesta zkopírována", "Path copied"), items.Count == 1 ? items[0].Path : Items(items.Count));
        return Task.CompletedTask;
    }

    private static Task OpenTerminalAsync(IReadOnlyList<FileItem> items)
    {
        var first = items[0];
        var folder = first.IsDirectory ? first.Path : Path.GetDirectoryName(first.Path)!;
        try
        {
            // Windows Terminal, if installed (app execution alias).
            Process.Start(new ProcessStartInfo("wt.exe", $"-d {QuotePath(folder)}") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoExit") { WorkingDirectory = folder, UseShellExecute = true });
        }
        return Task.CompletedTask;
    }

    /// <summary>Quotes a folder for a command line; "C:\" would otherwise escape the closing quote.</summary>
    private static string QuotePath(string folder) =>
        "\"" + (folder.EndsWith('\\') ? folder + "." : folder) + "\"";
}

using static FileAction.Core.Loc;

namespace FileAction.Core;

public static class OutputPaths
{
    /// <summary>Lets an isolated worker record the exact temporary paths it owns before creating them.</summary>
    public static event Action<string, bool>? TemporaryPathCreated;

    /// <summary>"dir/name.ext", or "dir/name (2).ext" … if that already exists.</summary>
    public static string Unique(string dir, string baseName, string ext)
    {
        ext = ext.TrimStart('.');
        var suffix = ext.Length == 0 ? "" : "." + ext;
        var candidate = Path.Combine(dir, baseName + suffix);
        for (var i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{baseName} ({i}){suffix}");
        return candidate;
    }

    public static string UniqueDirectory(string dir, string name) => Unique(dir, name, "");

    /// <summary>Publishes a whole generated folder only after all of its contents succeed.</summary>
    public static async Task<string> ProduceDirectoryAsync(string dir, string baseName, Func<string, Task> produce)
    {
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{baseName}.{Guid.NewGuid().ToString("N")[..8]}.part");
        TemporaryPathCreated?.Invoke(Path.GetFullPath(temp), true);
        try
        {
            Directory.CreateDirectory(temp);
            await produce(temp).ConfigureAwait(false);
            while (true)
            {
                var final = UniqueDirectory(dir, baseName);
                try
                {
                    Directory.Move(temp, final);
                    return final;
                }
                catch (IOException) when (File.Exists(final) || Directory.Exists(final)) { }
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Runs <paramref name="produce"/> against a hidden temporary path in <paramref name="dir"/> and,
    /// when it succeeds, moves the file to a unique final name. Partial output is removed on failure.
    /// </summary>
    public static async Task<string> ProduceAsync(string dir, string baseName, string ext, Func<string, Task> produce)
    {
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{baseName}.{Guid.NewGuid().ToString("N")[..8]}.part.{ext}");
        TemporaryPathCreated?.Invoke(Path.GetFullPath(temp), false);
        try
        {
            await produce(temp).ConfigureAwait(false);
            if (!File.Exists(temp))
                throw new ConversionException(T("Převod nevytvořil žádný výstup.", "The conversion produced no output."));
            while (true)
            {
                var final = Unique(dir, baseName, ext);
                try
                {
                    File.Move(temp, final);
                    return final;
                }
                catch (IOException) when (File.Exists(final) || Directory.Exists(final))
                {
                    // Another conversion may publish the same name between Unique and Move.
                }
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Peel", Guid.NewGuid().ToString("N"));
        TemporaryPathCreated?.Invoke(Path.GetFullPath(dir), true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class ConversionException(string message, Exception? inner = null) : Exception(message, inner);

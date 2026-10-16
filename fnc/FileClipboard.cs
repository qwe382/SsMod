using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SsMod.Fnc;

// Кладём в системный буфер обмена настоящий файл (не текст), минуя движок:
// его буфер умеет только текст (SDL_SetClipboardText), файлы он не умеет.
//
//   Linux  — wl-copy (Wayland) / xclip / xsel, тип text/uri-list;
//   Windows — CF_HDROP через WinAPI;
//   macOS   — osascript + NSPasteboard.
//
// Чтение симметрично: сначала пробуем системный буфер, потом отдаём движку.
public static class FileClipboard
{
    // ---------- Запись ----------

    public static bool SetFile(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                return false;

            if (OperatingSystem.IsWindows())
                return WindowsSet(path);
            if (OperatingSystem.IsMacOS())
                return MacSet(path);

            return LinuxSet(path);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SsMod] clipboard set file failed: {e.Message}");
            return false;
        }
    }

    // ---------- Чтение ----------

    public static string? GetFile()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsGet();
            if (OperatingSystem.IsMacOS())
                return null; // на macOS оставляем движку/тексту

            return LinuxGet();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SsMod] clipboard get file failed: {e.Message}");
            return null;
        }
    }

    // file://-URI или путь -> локальный путь, если файл существует.
    public static string? NormalizePath(string raw)
    {
        var line = raw.Trim().Trim('"');
        if (line.Length == 0)
            return null;

        if (line.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri))
                return null;

            line = uri.LocalPath;
        }

        return File.Exists(line) ? line : null;
    }

    // ---------- Linux ----------

    private static bool Wayland()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    private static bool LinuxSet(string path)
    {
        var uri = new Uri(path).AbsoluteUri + "\r\n";
        // Для GNOME/KDE «скопированный файл» — это список URI.
        var gnome = "copy\n" + new Uri(path).AbsoluteUri;

        if (Wayland())
        {
            if (RunSet("wl-copy", new[] { "--type", "text/uri-list" }, uri))
                return true;
        }

        if (RunSet("xclip", new[] { "-selection", "clipboard", "-t", "text/uri-list" }, uri))
            return true;

        if (RunSet("wl-copy", new[] { "--type", "x-special/gnome-copied-files" }, gnome))
            return true;

        // Последний шанс — просто путь текстом (тогда сработает разбор пути).
        return RunSet("xsel", new[] { "-ib" }, path);
    }

    private static string? LinuxGet()
    {
        string? outp = null;

        if (Wayland())
            outp = RunGet("wl-paste", new[] { "--type", "text/uri-list" });

        outp ??= RunGet("xclip", new[] { "-selection", "clipboard", "-o", "-t", "text/uri-list" });

        outp ??= Wayland()
            ? RunGet("wl-paste", new[] { "--type", "x-special/gnome-copied-files" })
            : RunGet("xclip", new[] { "-selection", "clipboard", "-o", "-t", "x-special/gnome-copied-files" });

        if (string.IsNullOrWhiteSpace(outp))
            return null;

        foreach (var line in outp.Split('\n'))
        {
            var path = NormalizePath(line);
            if (path != null)
                return path;
        }

        return null;
    }

    // ---------- Windows (CF_HDROP) ----------

    private const uint CfHDrop = 15;
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    private static bool WindowsSet(string path)
    {
        // DROPFILES (20 байт) + список файлов в UTF-16, завершённый двумя нулями.
        var files = Encoding.Unicode.GetBytes(path + "\0\0");
        var size = 20 + files.Length;

        var handle = GlobalAlloc(GmemMoveable, (UIntPtr) size);
        if (handle == IntPtr.Zero)
            return false;

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }

        try
        {
            var header = new byte[20];
            BitConverter.GetBytes(20).CopyTo(header, 0);   // pFiles — смещение списка
            BitConverter.GetBytes(1).CopyTo(header, 16);   // fWide = TRUE
            Marshal.Copy(header, 0, ptr, 20);
            Marshal.Copy(files, 0, ptr + 20, files.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            GlobalFree(handle);
            return false;
        }

        try
        {
            EmptyClipboard();
            if (SetClipboardData(CfHDrop, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
        }
        finally
        {
            CloseClipboard();
        }

        return true;
    }

    private static string? WindowsGet()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            var handle = GetClipboardData(CfHDrop);
            if (handle == IntPtr.Zero)
                return null;

            var length = DragQueryFileW(handle, 0xFFFFFFFF, null, 0);
            if (length == 0)
                return null;

            var sb = new StringBuilder((int) length + 1);
            DragQueryFileW(handle, 0, sb, (uint) sb.Capacity);
            var path = sb.ToString();

            return File.Exists(path) ? path : null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    // ---------- macOS ----------

    private static bool MacSet(string path)
    {
        var script = $"set the clipboard to POSIX file \"{path.Replace("\"", "\\\"")}\"";
        return RunSet("osascript", new[] { "-e", script }, "");
    }

    // ---------- Запуск внешних команд ----------

    private static bool RunSet(string cmd, string[] args, string data)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
                return false;

            process.StandardInput.Write(data);
            process.StandardInput.Close();

            // wl-copy/xclip уходят в фон владельцем буфера — тайм-аут это норма.
            if (!process.WaitForExit(1500))
                return true;

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? RunGet(string cmd, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}

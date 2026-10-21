using System;
using System.IO;

namespace SsMod.Fnc;

// Свой лог-файл рядом с логами лаунчера.
//
// В client.stdout.log лаунчер успевает записать только первые 32 КБ потока
// (дальше файл обрезается), поэтому конец сессии с диагностикой мода там не
// виден вообще. Пишем туда же, куда и раньше (Console.WriteLine), плюс в файл
// ~/.local/share/Space Station 14/ssmod.log.
public static class SsLog
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private static string? _path;
    private static bool _failed;

    public static string FilePath => _path ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "Space Station 14", "ssmod.log");

    public static void Write(string message)
    {
        Console.WriteLine(message);

        if (_failed)
            return;

        try
        {
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
                File.Delete(FilePath);

            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
            // Файл недоступен — не роняем мод и не спамим: пишем только в stdout.
            _failed = true;
        }
    }
}

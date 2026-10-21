using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace SsMod.Fnc;

// Система конфигов мода.
//
// Настройки хранятся как текст «ключ=значение» (по строке на ключ). Этот текст
// можно по желанию сжать (brotli) и/или закодировать в base64, после чего
// скопировать в буфер обмена или сохранить/загрузить файлом.
//
// Схема кодирования одна и та же для буфера и для файла: в буфер и в файл
// кладётся ровно то, что показано в окне.
public static class Config
{
    // Заголовок в сыром виде — упрощает распознавание своего формата.
    public const string Header = "SSMOD-CONFIG v1";

    private sealed class Entry
    {
        public string Key = "";
        public Func<string> Get = () => "";
        public Action<string> Set = _ => { };
    }

    private static readonly List<Entry> Entries = new();
    private static bool _registered;

    // Состояние переключателей кодирования: переживает пересоздание вкладки.
    private static bool _compress;
    private static bool _base64 = true;

    // ---------- Регистрация настроек ----------

    // Ленивая регистрация: рефлексией обходим дерево настроек ESP и вручную
    // добавляем переключатели мода.
    public static void EnsureRegistered()
    {
        if (_registered)
            return;

        _registered = true;

        RegisterTree("esp", typeof(EspSettings), null);

        // Фрейм «Персонажи»: таймер таблицы.
        RegisterTree("characters", typeof(CharacterSettings), null);

        // Стрельба gun-bot'а (вкладка Gun-bot): поведение стрельбы.
        // Включение самой наводки живёт отдельным ключом gunbot.aim ниже.
        RegisterTree("aimbot", typeof(AimBotSettings), null);


        Add("melbot",
            () => IoCManager.Resolve<IOverlayManager>().HasOverlay<MelBotOverlay>() ? "true" : "false",
            v => MelBot.Toggle(ParseBool(v)));

        Add("melbot.auto",
            () => MelBotOverlay.AutoAttack ? "true" : "false",
            v => MelBotOverlay.AutoAttack = ParseBool(v));

        Add("melbot.lmb",
            () => MelBotOverlay.AutoAttackLmb ? "true" : "false",
            v => MelBotOverlay.AutoAttackLmb = ParseBool(v));

        Add("cryobot",
            () => IoCManager.Resolve<IOverlayManager>().HasOverlay<CryoBotOverlay>() ? "true" : "false",
            v => CryoBot.Toggle(ParseBool(v)));

        Add("cryobot.combat",
            () => CryoBotOverlay.CombatTime.ToString(CultureInfo.InvariantCulture),
            v =>
            {
                if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    CryoBotOverlay.CombatTime = Math.Clamp(seconds, 0.1f, 30f);
            });

        Add("cryobot.pod",
            () => CryoBotOverlay.PodTime.ToString(CultureInfo.InvariantCulture),
            v =>
            {
                if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    CryoBotOverlay.PodTime = Math.Clamp(seconds, 0.1f, 30f);
            });

        Add("cameraLock",
            () => CameraLock.Enabled ? "true" : "false",
            v => CameraLock.Toggle(ParseBool(v)));

        Add("quickFlush",
            () => QuickFlush.Enabled ? "true" : "false",
            v => QuickFlush.Toggle(ParseBool(v)));

        Add("gunbot.aim",
            () => GunBot.Aim ? "true" : "false",
            v => GunBot.SetAim(ParseBool(v)));
    }

    // ---------- Конфиг по умолчанию ----------

    // Всё основное включено. Зовётся один раз на старте (Patches/StartupPatch)
    // до того, как пользователь применит свой конфиг из буфера или файла:
    // дефолт — это стартовое состояние, а не сохранённый профиль.
    public static void ApplyDefaults()
    {
        EnsureRegistered();

        Step("попапы урона", () => DamagePopup.Toggle(true));

        // Каждый шаг отдельно: если один оверлей не поднимется, остальные всё
        // равно включатся, а причина попадёт в лог.
        //
        // Мелбот включён, но автоудар — нет: бот только показывает цель,
        // вложенные настройки автоудара из окна убраны.
        Step("мелбот", () =>
        {
            MelBot.Toggle(true);
            MelBotOverlay.AutoAttack = false;
            MelBotOverlay.AutoAttackLmb = false;
        });

        // Точки наводки gun-bot'а. Крио-бот на старте не включаем: он бьёт сам.
        Step("наводка", () => GunBot.SetAim(true));

        Step("блокировка камеры", () => CameraLock.Toggle(true));
        Step("без эффектов", () => NoEffects.Toggle(true));
        Step("быстрый смыв", () => QuickFlush.Toggle(true));

        // ESP включён; категории и полоски в нём уже включены по умолчанию.
        Step("ESP", () => Esp.Toggle(true));

        Console.WriteLine("[SsMod] конфиг по умолчанию: основные функции включены");
    }

    private static void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            // Пишем в stdout: stderr лаунчер не всегда сохраняет.
            Console.WriteLine($"[SsMod] дефолт: {name} не включился: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Add(string key, Func<string> get, Action<string> set)
    {
        Entries.Add(new Entry { Key = key, Get = get, Set = set });
    }

    // Обход публичных полей типа. instance == null — статический тип (EspSettings),
    // иначе — конкретный объект (категория или полоска).
    private static void RegisterTree(string prefix, Type type, object? instance)
    {
        var flags = BindingFlags.Public |
                    (instance == null ? BindingFlags.Static : BindingFlags.Instance);

        foreach (var field in type.GetFields(flags))
        {
            if (field.Name == "Title")
                continue;

            var key = $"{prefix}.{field.Name.ToLowerInvariant()}";
            var fieldType = field.FieldType;

            if (IsLeaf(fieldType))
            {
                var f = field;
                var obj = instance;
                Add(key,
                    () => Format(f.GetValue(obj)),
                    v => f.SetValue(obj, Parse(v, fieldType)));
                continue;
            }

            // Сложный тип — уходим внутрь (категории, полоски).
            var value = field.GetValue(instance);
            if (value != null)
                RegisterTree(key, fieldType, value);
        }
    }

    private static bool IsLeaf(Type t)
    {
        return t == typeof(bool) || t == typeof(float) || t == typeof(int) ||
               t == typeof(double) || t == typeof(string) || t == typeof(Vector2) ||
               t == typeof(Color) || t.IsEnum;
    }

    // ---------- Сериализация значений ----------

    private static string Format(object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case bool b:
                return b ? "true" : "false";
            case float f:
                return f.ToString("R", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("R", CultureInfo.InvariantCulture);
            case int i:
                return i.ToString(CultureInfo.InvariantCulture);
            case Vector2 v:
                return $"{F(v.X)},{F(v.Y)}";
            case Color c:
                return $"{F(c.R)},{F(c.G)},{F(c.B)},{F(c.A)}";
            case Enum e:
                return e.ToString();
            default:
                return value.ToString() ?? "";
        }
    }

    private static object Parse(string raw, Type t)
    {
        var s = raw.Trim();

        if (t == typeof(bool))
            return ParseBool(s);
        if (t == typeof(float))
            return float.Parse(s, CultureInfo.InvariantCulture);
        if (t == typeof(double))
            return double.Parse(s, CultureInfo.InvariantCulture);
        if (t == typeof(int))
            return int.Parse(s, CultureInfo.InvariantCulture);
        if (t == typeof(string))
            return s;
        if (t == typeof(Vector2))
        {
            var p = s.Split(',');
            return new Vector2(float.Parse(p[0], Inv), float.Parse(p[1], Inv));
        }
        if (t == typeof(Color))
        {
            var p = s.Split(',');
            return new Color(
                float.Parse(p[0], Inv),
                float.Parse(p[1], Inv),
                float.Parse(p[2], Inv),
                p.Length > 3 ? float.Parse(p[3], Inv) : 1f);
        }
        if (t.IsEnum)
            return Enum.Parse(t, s, true);

        return s;
    }

    private static string F(float v) => v.ToString("R", Inv);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static bool ParseBool(string s)
    {
        s = s.Trim().ToLowerInvariant();
        return s is "true" or "1" or "on" or "да" or "yes";
    }

    // ---------- Текст ключ-значение ----------

    // Собираем текущее состояние настроек в текст.
    public static string BuildText()
    {
        EnsureRegistered();

        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');

        foreach (var entry in Entries)
        {
            string value;
            try
            {
                value = entry.Get();
            }
            catch
            {
                value = "";
            }

            sb.Append(entry.Key).Append('=').Append(value).Append('\n');
        }

        return sb.ToString();
    }

    // Применяем текст к настройкам. Возвращает, сколько ключей удалось применить.
    private static int Apply(string text, out int total)
    {
        EnsureRegistered();

        var applied = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
                continue;

            var idx = line.IndexOf('=');
            if (idx <= 0)
                continue;

            var key = line[..idx].Trim().ToLowerInvariant();
            var value = line[(idx + 1)..].Trim();

            var entry = Entries.FirstOrDefault(e => e.Key == key);
            if (entry == null)
                continue;

            try
            {
                entry.Set(value);
                applied++;
            }
            catch
            {
                // Битое значение — пропускаем строку, остальное применяем.
            }
        }

        total = Entries.Count;
        return applied;
    }

    // ---------- Кодирование / декодирование ----------

    // text -> (gzip?) -> (base64?). base64 применяется, если он включён или
    // если текст сжат (сжатые байты нельзя показать как обычный текст).
    public static string Encode(string text, bool compress, bool base64)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        if (compress)
            bytes = Brotli(bytes);

        if (base64 || compress)
            return Convert.ToBase64String(bytes);

        return text;
    }

    // Пробуем разобрать ввод в любом из вариантов и применить.
    public static string ApplyFromInput(string input)
    {
        EnsureRegistered();

        input = input.Trim();
        if (input.Length == 0)
            return "пусто";

        foreach (var candidate in Candidates(input))
        {
            var applied = Apply(candidate, out var total);
            if (applied > 0)
                return $"применено {applied}/{total}";
        }

        return "не удалось разобрать";
    }

    // Возможные расшифровки: base64, base64+gzip (старый формат),
    // base64+brotli и как есть.
    private static IEnumerable<string> Candidates(string input)
    {
        if (TryBase64(input, out var bytes))
        {
            // base64 от несжатого текста.
            yield return Encoding.UTF8.GetString(bytes);

            // base64 от старого gzip-варианта.
            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                var gunzipped = TryGunzip(bytes);
                if (gunzipped != null)
                    yield return Encoding.UTF8.GetString(gunzipped);
            }

            // base64 от brotli.
            var brotli = TryBrotli(bytes);
            if (brotli != null)
                yield return Encoding.UTF8.GetString(brotli);
        }

        yield return input;
    }

    private static bool TryBase64(string input, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        // Base64 длиннее и «плотнее» обычного текста, но проверим без гадания.
        var compact = input.Replace("\n", "").Replace("\r", "").Replace(" ", "");

        try
        {
            bytes = Convert.FromBase64String(compact);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Старый формат на gzip — оставлен, чтобы прежние конфиги читались.
    private static byte[]? TryGunzip(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryBrotli(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            brotli.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    // Brotli: специализирован под текст, даёт заметно меньше символов на выходе,
    // чем gzip. Качество 11, окно 24 — максимум сжатия.
    private static byte[] Brotli(byte[] bytes)
    {
        var buffer = new byte[BrotliEncoder.GetMaxCompressedLength(bytes.Length)];
        if (!BrotliEncoder.TryCompress(bytes, buffer, out var written, 11, 24))
            return bytes;

        return buffer[..written];
    }

    // ---------- Файлы ----------

    // Пишем конфиг во временную папку (кросс-платформенно) и возвращаем путь.
    // Никаких диалогов: путь копируется в буфер, вставка читает его оттуда.
    public static string SaveTemp(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ssmod");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"ssmod-config-{DateTime.Now:yyyyMMdd-HHmmss}.ssmod");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    // Ищем в буфере ссылку на файл. Файловые менеджеры кладут путь как
    // file://-URI (иногда со списком строк), поэтому разбираем и это.
    public static string? FindFileInClipboard(string clip)
    {
        foreach (var rawLine in clip.Split('\n'))
        {
            var path = FileClipboard.NormalizePath(rawLine);
            if (path != null)
                return path;
        }

        return null;
    }

    // ---------- Вкладка настроек ----------

    public static Control BuildTab()
    {
        EnsureRegistered();

        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        var status = new Label { Text = "" };

        var preview = new TextEdit
        {
            Editable = true,
            MinWidth = 480,
            MinHeight = 220,
        };

        void Show(string text) => preview.TextRope = new Rope.Leaf(text);

        // Текущие настройки в выбранном формате.
        string Current() => Encode(BuildText(), _compress, _base64);

        var copy = Button("Копировать", () =>
        {
            var text = Current();
            Show(text);
            IoCManager.Resolve<IClipboardManager>().SetText(text);
            status.Text = "скопировано в буфер";
        });

        var paste = Button("Вставить", async () =>
        {
            var clip = await IoCManager.Resolve<IClipboardManager>().GetText();
            var path = FileClipboard.GetFile() ?? FindFileInClipboard(clip);
            var text = path != null ? await File.ReadAllTextAsync(path, Encoding.UTF8) : clip;
            Show(text);
            var result = ApplyFromInput(text);
            status.Text = path != null ? $"файл: {path} — {result}" : result;
        });

        var copyFile = Button("Копировать в файл", () =>
        {
            var text = Current();
            Show(text);
            var path = SaveTemp(text);

            if (FileClipboard.SetFile(path))
                status.Text = $"файл в буфере: {path}";
            else
            {
                // Системного файлового буфера нет — кладём хотя бы путь текстом.
                IoCManager.Resolve<IClipboardManager>().SetText(path);
                status.Text = $"путь в буфере: {path}";
            }
        });

        var pasteFile = Button("Вставить из файла", async () =>
        {
            var clip = await IoCManager.Resolve<IClipboardManager>().GetText();
            var path = FileClipboard.GetFile() ?? FindFileInClipboard(clip);
            if (path == null)
            {
                status.Text = "в буфере нет файла";
                return;
            }

            var text = await File.ReadAllTextAsync(path, Encoding.UTF8);
            Show(text);
            status.Text = $"файл: {path} — {ApplyFromInput(text)}";
        });

        var row1 = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        row1.AddChild(copy);
        row1.AddChild(copyFile);

        var row2 = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        row2.AddChild(paste);
        row2.AddChild(pasteFile);

        // Всё остальное — в разворачиваемом списке.
        var options = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        var compressCheck = new CheckBox { Text = "Сжимать (brotli)", Pressed = _compress };
        compressCheck.OnToggled += args => _compress = args.Pressed;

        var base64Check = new CheckBox { Text = "Кодировать в base64", Pressed = _base64 };
        base64Check.OnToggled += args => _base64 = args.Pressed;

        options.AddChild(compressCheck);
        options.AddChild(base64Check);
        options.AddChild(new Label { Text = "Содержимое (ключ=значение)" });
        options.AddChild(preview);

        var body = new CollapsibleBody();
        body.AddChild(options);

        root.AddChild(row1);
        root.AddChild(row2);
        root.AddChild(status);
        root.AddChild(new Collapsible("Кодирование", body));

        return root;
    }

    private static Button Button(string text, Action onPressed)
    {
        var button = new Button { Text = text };
        button.OnPressed += _ => onPressed();
        return button;
    }
}

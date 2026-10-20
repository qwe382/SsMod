using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;
using Robust.Shared.Maths;

namespace SsMod.Fnc;

// Картинки для кнопок фреймов. Свои иконки не рисуем — берём готовые из
// ресурсов игры (те же пути, что контент указывает в XAML).
//
// Поддерживаются два вида путей:
//   * картинка целиком: "/Textures/Interface/VerbIcons/light.svg.192dpi.png"
//     (Svg-иконки лежат там в трёхкратном размере — отсюда суффикс .192dpi.png);
//   * кадр из спрайтового листа: "/Textures/Objects/Tools/handheld_mass_scanner.rsi#icon"
//     (без "#имя" берётся состояние "icon").
internal static class Icons
{
    // Замок: кнопка «держим подсветку» (заменяет прежнюю лампочку).
    public const string Lock = "/Textures/Interface/VerbIcons/lock.svg.192dpi.png";

    // «На 2 секунды» — часы.
    public const string Clock = "/Textures/Interface/VerbIcons/clock.svg.192dpi.png";

    // Текстура грузится один раз: GetResource кеширует сам, но нам важно не
    // пытаться загрузить битый путь каждый кадр.
    private static readonly Dictionary<string, Texture?> Cache = new();

    // null — картинки на этой сборке нет; тогда кнопка откатывается на квадрат.
    public static Texture? Get(string path)
    {
        if (Cache.TryGetValue(path, out var cached))
            return cached;

        Texture? texture = null;

        try
        {
            texture = Load(path);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SsMod] иконка {path} не загрузилась: {e.Message}");
        }

        Cache[path] = texture;
        return texture;
    }

    private static Texture? Load(string path)
    {
        var cache = IoCManager.Resolve<IResourceCache>();

        // Спрайтовый лист: путь#состояние.
        var hash = path.IndexOf('#');
        if (hash >= 0 || path.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase))
        {
            var rsiPath = hash >= 0 ? path[..hash] : path;
            var state = hash >= 0 ? path[(hash + 1)..] : "icon";

            var rsi = cache.GetResource<RSIResource>(rsiPath).RSI;
            if (!rsi.TryGetState(state, out var found))
            {
                Console.WriteLine($"[SsMod] иконка: в {rsiPath} нет состояния «{state}»");
                return null;
            }

            return found.Frame0;
        }

        return cache.GetResource<TextureResource>(path).Texture;
    }

    // Кнопка-картинка нужного размера в пикселях. Если картинки нет — серый
    // квадрат с подсказкой: кнопка остаётся рабочей, просто некрасивой.
    public static TextureButton Button(string path, float size, string tooltip, Action onPressed)
    {
        var button = new TextureButton
        {
            ToolTip = tooltip,
            // Без этого BoxContainer растянул бы кнопку по высоте строки и
            // квадрат превратился бы в прямоугольник.
            VerticalAlignment = Control.VAlignment.Center,
        };

        if (Get(path) is { } texture)
        {
            button.TextureNormal = texture;
            button.Scale = new Vector2(size / texture.Size.X, size / texture.Size.Y);
        }
        else
        {
            button.TextureNormal = Texture.White;
            button.Scale = new Vector2(size, size);
            button.Modulate = new Color(0.45f, 0.45f, 0.45f);
        }

        button.OnPressed += _ => onPressed();
        return button;
    }

    // Квадрат-метка: цветная кнопка без текста (один белый пиксель, растянутый
    // до нужного размера и подкрашенный Modulate).
    public static TextureButton Square(Color color, float size, string tooltip, Action onPressed)
    {
        var button = new TextureButton
        {
            TextureNormal = Texture.White,
            Scale = new Vector2(size, size),
            Modulate = color,
            ToolTip = tooltip,
            VerticalAlignment = Control.VAlignment.Center,
        };

        button.OnPressed += _ => onPressed();
        return button;
    }
}

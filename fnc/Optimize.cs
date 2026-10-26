using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;

namespace SsMod.Fnc;

// Вкладка «Оптимизация»: переключатели настроек клиента, которые режут
// нагрузку на графику. Каждая галка ставит CVar игры: включил — применяем
// экономное значение, выключил — возвращаем дефолт. Значения не сохраняются,
// после перезапуска игры всё как было.
//
// Имена и дефолты сверены по 276 / fish340 / 290; чего нет в версии —
// пропускается (IsCVarRegistered).
public static class Optimize
{
    // CVar, значение-оптимизация, значение-по-умолчанию.
    private static readonly (string Name, bool Optimized, bool Default)[] Toggles =
    {
        // Параллакс (звёзды/фон) — дорогой шейдер.
        ("parallax.enabled", false, true),
        // Дешёвая отрисовка параллакса вместо полной (нет в 276).
        ("parallax.low_quality", true, false),
        // Мягкие тени от источников света.
        ("light.soft_shadows", false, true),
        // Размытие света.
        ("light.blur", false, true),
        // Амбиент-окклюзия (затемнение углов).
        ("light.ambient_occlusion", false, true),
        // Обводка предметов под курсором.
        ("outline.enabled", false, true),
        // VSync ограничивает FPS частотой монитора.
        ("display.vsync", false, true),
        // Счётчик FPS — не оптимизация, но для проверки пользы.
        ("hud.fps_counter_visible", true, false),
    };

    private static readonly IConfigurationManager Cfg = IoCManager.Resolve<IConfigurationManager>();

    public static Control BuildTab()
    {
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        foreach (var (name, optimized, def) in Toggles)
        {
            // В этой версии клиента настройки нет — галку не показываем.
            if (!Cfg.IsCVarRegistered(name))
                continue;

            var cvar = name;
            var on = optimized;
            var off = def;

            var check = new CheckBox { Text = Label(cvar), Pressed = Cfg.GetCVar<bool>(cvar) == on };
            check.OnToggled += args => Cfg.SetCVar(cvar, args.Pressed ? on : off);
            box.AddChild(check);
        }

        return box;
    }

    // Человекочитаемая подпись вместо имени CVar.
    private static string Label(string cvar) => cvar switch
    {
        "parallax.enabled" => "Без параллакса (звёзды/фон)",
        "parallax.low_quality" => "Дешёвый параллакс (если фон остался)",
        "light.soft_shadows" => "Без мягких теней света",
        "light.blur" => "Без размытия света",
        "light.ambient_occlusion" => "Без затемнения углов",
        "outline.enabled" => "Без обводки предметов",
        "display.vsync" => "Без VSync (больше FPS, возможен разрыв кадра)",
        "hud.fps_counter_visible" => "Счётчик FPS",
        _ => cvar,
    };
}

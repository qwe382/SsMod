using Content.Client.Entry;
using HarmonyLib;
using Robust.Client.Graphics;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using SsMod.Fnc;
using SsMod.Ui;

namespace SsMod.Patches;

[HarmonyPatch(typeof(EntryPoint), nameof(EntryPoint.PostInit))]
public static class StartupPatch
{
    private static bool _initialized;
    private static ISawmill? _sawmill;

    public static void Postfix()
    {
        if (_initialized)
            return;

        _initialized = true;

        try
        {
            _sawmill ??= IoCManager.Resolve<ILogManager>().GetSawmill("ssmod");
            _sawmill.Info("PostInit: SsMod активен");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SsMod] PostInit: лог не поднялся: {e.Message}");
        }

        // Каждый шаг отдельно. Раньше всё шло одним try: одно исключение (например,
        // в оверлее) молча обрывало остальные шаги — и стартовый конфиг не
        // применялся вообще. Ошибку печатаем в stdout: stderr лаунчер не сохраняет.
        Step("всплывашки роутера", () =>
        {
            var overlays = IoCManager.Resolve<IOverlayManager>();
            if (!overlays.HasOverlay<RouterPopupOverlay>())
                overlays.AddOverlay(new RouterPopupOverlay());
        });

        // Точки наводки висят всегда, независимо от галочки «Аимбот».
        Step("точки наводки", () =>
        {
            var overlays = IoCManager.Resolve<IOverlayManager>();
            if (!overlays.HasOverlay<GunBotOverlay>())
                overlays.AddOverlay(new GunBotOverlay());
        });

        // Дуга патронов у курсора. Внутри сама проверяет галочку из вкладки
        // «Визуалы», поэтому висит всегда.
        Step("дуга патронов", () =>
        {
            var overlays = IoCManager.Resolve<IOverlayManager>();
            if (!overlays.HasOverlay<AmmoOverlay>())
                overlays.AddOverlay(new AmmoOverlay());
        });

        // Трейсы от себя до персонажей (цвет по приоритету).
        Step("трейсы", () =>
        {
            var overlays = IoCManager.Resolve<IOverlayManager>();
            if (!overlays.HasOverlay<TracesOverlay>())
                overlays.AddOverlay(new TracesOverlay());
        });

        // Круг-таймер предикта взаимодействий. Внутри сам проверяет, есть ли
        // ожидание, поэтому висит всегда.
        Step("предикт взаимодействий", () =>
        {
            var overlays = IoCManager.Resolve<IOverlayManager>();
            if (!overlays.HasOverlay<InteractQueueOverlay>())
                overlays.AddOverlay(new InteractQueueOverlay());
        });

        // Стартовый конфиг. Оверлеи внутри берут системы лениво, поэтому
        // создание здесь безопасно (в PostInit IEntityManager.System<T>() ещё
        // падает, см. комментарии в MelBot/GunBot/Esp/DamagePopup).
        Step("дефолтный конфиг", Config.ApplyDefaults);

        // SsModUi.Create();
    }

    private static void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SsMod] PostInit: {name} — ошибка: {e.GetType().Name}: {e.Message}");
        }
    }
}

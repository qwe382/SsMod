using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using SsMod.Debug;
using SsMod.Fnc;

namespace SsMod.Ui;

public sealed class SsModWindow : DefaultWindow
{
    // Одновременно открыт ровно один фрейм: в контейнер попадает только
    // выбранный, остальные не создаются до выбора.
    // Фабрики, а не готовые Control: у контрола один родитель, общий экземпляр
    // нельзя добавить в второе окно.
    private static readonly (string Title, Func<Control> Build)[] Categories =
    {
        ("Фрейм 1", BuildFrame1),
        ("ESP", Esp.BuildTab),
        ("Конфиг", Config.BuildTab),
        ("Дебаг", BuildDebug),
        ("Твики", BuildTweaks),
    };

    private readonly BoxContainer _frameHost;

    public SsModWindow()
    {
        Title = "SsMod";

        // ВАЖНО: Contents у DefaultWindow — это Control-якорь (ContentsContainer
        // в XAML), а не контейнер. Всё оборачиваем в собственные контейнеры,
        // иначе контролы накладываются друг на друга.
        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        var columns = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };

        // Слева — выбор фрейма в скролле. Ширину задаём MinWidth на контейнере.
        var nav = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            MinWidth = 170,
        };
        var navScroll = new ScrollContainer
        {
            HScrollEnabled = false,
            // Без ReturnMeasure скролл в MeasureOverride возвращает Vector2.Zero
            // и схлопывается в ноль. Так во всех рабочих окнах игры.
            ReturnMeasure = true,
            // Ширина колонки — ровно по кнопкам (MinWidth у nav). С HorizontalExpand
            // скролл делил бы окно пополам с правой частью.
            HorizontalExpand = false,
            VerticalExpand = true,
        };
        navScroll.AddChild(nav);

        for (var i = 0; i < Categories.Length; i++)
        {
            var index = i;
            var btn = new Button { Text = Categories[i].Title };
            btn.OnPressed += _ => OpenFrame(index);
            nav.AddChild(btn);
        }

        // Справа — открытый фрейм, обёрнутый в скролл.
        _frameHost = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
        };
        var frameScroll = new ScrollContainer
        {
            HScrollEnabled = false,
            ReturnMeasure = true,
            HorizontalExpand = true,
            VerticalExpand = true,
        };
        frameScroll.AddChild(_frameHost);

        columns.AddChild(navScroll);
        columns.AddChild(frameScroll);
        root.AddChild(columns);
        Contents.AddChild(root);

        OpenFrame(0);
    }

    private void OpenFrame(int index)
    {
        _frameHost.RemoveAllChildren();
        _frameHost.AddChild(Categories[index].Build());
    }

    private static Control BuildFrame1()
    {
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        // Переключатель Mel-Bot: линия до персонажа под курсором + автоудар.
        box.AddChild(MelBot.BuildControls());

        // Крио-бот: сам бьёт помеченных Make Enemy и чередует заходы в капсулу.
        box.AddChild(CryoBot.BuildControls());

        return box;
    }

    // Фрейм "Дебаг": переключатель "Block debug" живёт здесь,
    // фабрика чекбокса — в debug/WindowHooks.cs.
    private static Control BuildTweaks()
    {
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        var quickFlush = QuickFlush.BuildCheckBox();
        quickFlush.OnToggled += args => QuickFlush.Toggle(args.Pressed);
        box.AddChild(quickFlush);

        var cameraLock = CameraLock.BuildCheckBox();
        cameraLock.OnToggled += args => CameraLock.Toggle(args.Pressed);
        box.AddChild(cameraLock);

        var noEffects = NoEffects.BuildCheckBox();
        noEffects.OnToggled += args => NoEffects.Toggle(args.Pressed);
        box.AddChild(noEffects);

        var damagePopup = DamagePopup.BuildCheckBox();
        damagePopup.OnToggled += args => DamagePopup.Toggle(args.Pressed);
        box.AddChild(damagePopup);

        return box;
    }

    private static Control BuildDebug()
    {
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };

        var blockDebug = SsModWindowCheckbox.BuildBlockDebugCheckBox();
        blockDebug.OnToggled += args => PassabilityOverlay.Toggle(args.Pressed);
        box.AddChild(blockDebug);

        return box;
    }
}

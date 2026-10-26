using System.Numerics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;
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
        ("Персонажи", Characters.BuildTab),
        ("Другое", MassScanner.BuildTab),
        // Фрейм «Визуалы»: внутри свои вкладки — gun-bot, дуга патронов, трейсы.
        ("Визуалы", Visuals.BuildTab),
        ("Конфиг", Config.BuildTab),
        ("Дебаг", BuildDebug),
        ("Твики", BuildTweaks),
    };

    private readonly BoxContainer _frameHost;

    public SsModWindow()
    {
        Title = "SsMod";

        // Окно не должно ужиматься до нуля, когда в правой панели нет содержимого
        // (например, фрейм не собрался): иначе от окна остаётся узкая полоска
        // навигации и кажется, что «ничего не открывается».
        MinSize = new Vector2(620f, 400f);

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
        var title = Categories[index].Title;

        // Собираем фрейм ДО того, как убрать текущий: если сборка упадёт
        // (баг в фрейме, данные ещё не готовы), пользователь увидит ошибку, а не
        // пустую панель. Раньше RemoveAllChildren шёл первым, и одно исключение
        // оставляло окно «мёртвым» до перезапуска игры.
        Control built;

        try
        {
            built = Categories[index].Build();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] фрейм «{title}» не собрался: {e}");

            var error = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical };
            error.AddChild(new Label { Text = $"Фрейм «{title}» не открылся" });
            error.AddChild(new Label { Text = e.Message, FontColorOverride = new Color(1f, 0.45f, 0.45f) });
            built = error;
        }

        _frameHost.RemoveAllChildren();
        _frameHost.AddChild(built);
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

        var throughWalls = ThroughWalls.BuildCheckBox();
        throughWalls.OnToggled += args => ThroughWalls.Toggle(args.Pressed);
        box.AddChild(throughWalls);

        var autoGrille = AutoGrille.BuildCheckBox();
        autoGrille.OnToggled += args => AutoGrille.Toggle(args.Pressed);
        box.AddChild(autoGrille);

        // Предикт взаимодействий: клик вне радиуса ждёт цель, круг-таймер.
        box.AddChild(InteractQueue.BuildControls());

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

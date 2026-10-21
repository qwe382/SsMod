using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace SsMod.Fnc;

// Визуал боя: линия от персонажа до цели и вращающиеся дуги вокруг неё.
// Общий для мелбота и gun-bot'а — раньше это целиком жило внутри мелбота,
// из-за чего gun-bot не мог нарисовать то же самое.
//
// Различаются только цветом: мелбот — жёлто-оранжевый, gun-bot (аимбот) — синий.
internal static class CombatVisuals
{
    // Радиус дуг вокруг цели, в тайлах. Задан в мире, чтобы дуги облегали
    // персонажа одинаково на любом зуме.
    public const float ArcRadiusTiles = 0.5f;

    // Раствор дуги в градусах. Круг делится на 4 дуги с промежутками 60° между ними.
    public const float ArcSpan = 30f;
    public const int ArcCount = 4;
    public const float ArcSpin = 180f; // градусов в секунду

    // Сегментов на дугу: чем больше, тем глаже обводка.
    private const int SegmentsPerArc = 32;

    // Толщины в пикселях. Толщины у DrawLine в движке нет: он рисует GL_LINES
    // из двух вершин (Clyde.Rendering.cs:718), то есть всегда 1 px. Поэтому вся
    // толщина делается заливкой TriangleFan — см. ThickLine.
    public const float LineWidth = 2f;

    // Дуги мелбота просили сделать жирнее (было 4), потом ещё вдвое чётче.
    public const float ArcWidth = 12f;

    // Цвета визуала цели.
    public readonly record struct Style(Color Line, Color Arc);

    // Мелбот: жёлтая линия, оранжевые дуги (как было).
    public static readonly Style Melee =
        new(new Color(1f, 0.85f, 0.2f, 0.5f), new Color(1f, 0.45f, 0.15f, 0.3f));

    // Gun-bot: те же фигуры, но синие.
    public static readonly Style Ranged =
        new(new Color(0.45f, 0.7f, 1f, 0.6f), new Color(0.25f, 0.55f, 1f, 0.35f));

    public static void Draw(DrawingHandleScreen handle, Vector2 fromScreen, Vector2 toScreen,
        float arcRadius, Style style, TimeSpan now)
    {
        ThickLine(handle, fromScreen, toScreen, LineWidth, style.Line);
        DrawArcs(handle, toScreen, arcRadius, style.Arc, now);
    }

    // Экранная точка через ту же согласованную пару вьюпорта, что и LocalToWorld
    // (см. пояснения в MelBot): WorldToLocal + левый верхний угол вьюпорта.
    public static Vector2 Screen(in OverlayDrawArgs args, Vector2 world)
        => args.Viewport.WorldToLocal(world) + Compat.BoxTopLeft(args.ViewportBounds);

    // Радиус дуг в пикселях экрана. Берём у самой камеры: две соседние мировые
    // точки на расстоянии tiles тайлов дают в WorldToLocal экранное расстояние.
    public static float Radius(in OverlayDrawArgs args, Vector2 world, float tiles)
    {
        var center = args.Viewport.WorldToLocal(world);
        var edge = args.Viewport.WorldToLocal(world + new Vector2(tiles, 0f));
        return (edge - center).Length();
    }

    public static float ArcRadius(in OverlayDrawArgs args, Vector2 world)
        => Radius(args, world, ArcRadiusTiles);

    // Мир под курсором. Через EyeManager.PixelToMap не берём: он идёт через
    // _uiManager.MouseGetControl и молча отдаёт мир в нуле, если курсор мимо
    // вьюпорта; WorldToScreen не годится по масштабу (ViewportContainer делит
    // ещё и на _viewportResolution).
    public static Vector2 CursorWorld(in OverlayDrawArgs args, out ScreenCoordinates screenMouse)
    {
        screenMouse = IoCManager.Resolve<IInputManager>().MouseScreenPosition;
        var localMouse = screenMouse.Position - Compat.BoxTopLeft(args.ViewportBounds);
        return args.Viewport.LocalToWorld(localMouse).Position;
    }

    // Круг вокруг цели, разделённый на 4 дуги по 30°, разнесённые по окружности
    // и вращающиеся вокруг неё.
    //
    // Дуги обводятся толстыми отрезками: 4 * SegmentsPerArc коротких линий,
    // состыкованных в гладкую кривую.
    private static void DrawArcs(DrawingHandleScreen handle, Vector2 center, float radius, Color color, TimeSpan now)
    {
        // Спин по реальному времени: скорость не зависит от тиков сервера.
        var spin = (float) now.TotalSeconds * (ArcSpin * (MathF.PI / 180f));
        var span = ArcSpan * (MathF.PI / 180f);

        // Дуги разнесены по всей окружности: шаг = 360 / 4 = 90°,
        // между ними остаются пустые промежутки по 60°.
        var step = MathF.Tau / ArcCount;

        for (var i = 0; i < ArcCount; i++)
        {
            // Стартовый угол дуги: разносим по окружности и крутим общим спином.
            var start = spin + step * i;

            for (var s = 0; s < SegmentsPerArc; s++)
            {
                var a0 = start + span * s / SegmentsPerArc;
                var a1 = start + span * (s + 1) / SegmentsPerArc;
                ThickLine(handle, Point(center, a0, radius), Point(center, a1, radius), ArcWidth, color);
            }
        }
    }

    // Полный круг вокруг цели — подсветка из фрейма «Персонажи» (там круг
    // заметно больше дуг мелбота).
    public static void Circle(DrawingHandleScreen handle, Vector2 center, float radius, float width, Color color,
        int segments = 64)
    {
        var step = MathF.Tau / segments;
        var from = Point(center, 0f, radius);

        for (var i = 1; i <= segments; i++)
        {
            var to = Point(center, step * i, radius);
            ThickLine(handle, from, to, width, color);
            from = to;
        }
    }

    // Толстый отрезок заливкой: 4 вершины = 2 треугольника.
    public static void ThickLine(DrawingHandleScreen handle, Vector2 from, Vector2 to, float width, Color color)
    {
        var dir = to - from;
        var len = dir.Length();
        if (len <= 0.0001f)
            return;

        // Перпендикуляр к отрезку: половина толщины в каждую сторону.
        var normal = new Vector2(-dir.Y, dir.X) / len * (width / 2f);

        Span<Vector2> quad = stackalloc Vector2[4];
        quad[0] = from - normal;
        quad[1] = to - normal;
        quad[2] = to + normal;
        quad[3] = from + normal;

        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, quad, color);
    }

    private static Vector2 Point(Vector2 center, float angle, float radius)
    {
        // -Cos по Y: дуги крутятся в экранных координатах по часовой стрелке.
        return center + new Vector2(MathF.Sin(angle), -MathF.Cos(angle)) * radius;
    }
}

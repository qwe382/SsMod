using System.Numerics;
using HarmonyLib;
using Robust.Client.Graphics;
using Robust.Shared.Graphics;
using Robust.Shared.IoC;

namespace SsMod.Fnc;

// Блокировка радиуса обзора (зума камеры).
//
// Значение живёт здесь, в моде. Менять его может ТОЛЬКО ViewRadius.Set() —
// это единственная функция, которая пишет в камеру. Любые чужие записи отсекают
// гарды EyeZoomGuard (Eye.Zoom) и EyeScaleGuard (Eye.Scale).
// Схема та же, что у SsWrite/LightEnabledGuard в fnc/Fullbright.cs.
//
// Что учтено из исходников движка (Robust.Shared/Graphics/Eye.cs):
//   * Zoom — virtual property с телом, пишет в поле _scale;
//   * Scale — второй вход в то же _scale, поэтому закрыт тоже;
//   * FixedEye : Eye ничего не переопределяет, пат на базовый Eye покрывает всю камеру;
//   * весь игровой код (ZoomCommand, хоткеи, SharedEyeSystem.SetZoom,
//     EyeLerpingSystem) меняет зум только через Eye.Zoom = ...,
//     то есть перекрытие этих двух сеттеров — замкнутое;
//   * дефолт движка _scale = One/2, то есть Zoom == 2, а не 1 — стартовое
//     значение блокировка берёт из текущей камеры, а не из константы.

public static class ViewRadius
{
    public const float Min = 0.1f;
    public const float Max = 20f;

    // Значение мода. Публично только на чтение.
    public static Vector2 Value { get; private set; }

    // Блокировка взведена: гарды стоят и пропускают только наши записи.
    public static bool Locked { get; private set; }

    // Окно, в котором гарды нас пропускают.
    private static bool _writing;

    /// <summary>
    /// Единственная функция мода, которая может изменить значение.
    /// Поднимает _writing на время записи — только в это окно гарды её пропускают.
    /// </summary>
    public static void Set(Vector2 zoom)
    {
        Value = new Vector2(Math.Clamp(zoom.X, Min, Max), Math.Clamp(zoom.Y, Min, Max));

        _writing = true;
        try
        {
            IoCManager.Resolve<IEyeManager>().CurrentEye.Zoom = Value;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] view radius write failed: {e}");
        }
        finally
        {
            _writing = false;
        }
    }

    /// <summary>Умножить радиус обзора на коэффициент (колесо мыши).</summary>
    public static void Step(float factor)
    {
        var z = Value * factor;
        Set(z);
    }

    /// <summary>Взвести блокировку. Вызывается один раз на PostInit.</summary>
    internal static void Lock()
    {
        try
        {
            // Стартовое значение — текущий зум камеры, а не константа:
            // дефолт движка = 2, и ломать вид до первого чиху не надо.
            Value = IoCManager.Resolve<IEyeManager>().CurrentEye.Zoom;
            Locked = true;
            Console.WriteLine($"[SsMod] view radius: заблокировано на {Value.X:0.##}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] view radius lock failed: {e}");
        }
    }

    // Вопрос гарда: пускать запись или отсечь.
    internal static bool AllowWrite() => !Locked || _writing;
}

// Гард на Eye.Zoom: false — оригинальный сеттер не выполняется.
[HarmonyPatch(typeof(Eye), "set_Zoom")]
public static class EyeZoomGuard
{
    public static bool Prefix() => ViewRadius.AllowWrite();
}

// Гард на Eye.Scale — обратная величина к Zoom, но пишет в то же поле _scale.
[HarmonyPatch(typeof(Eye), "set_Scale")]
public static class EyeScaleGuard
{
    public static bool Prefix() => ViewRadius.AllowWrite();
}
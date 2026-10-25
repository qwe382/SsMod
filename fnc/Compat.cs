using System.Numerics;
using System.Reflection;
using System.Text;
using Content.Client.Gameplay;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace SsMod.Fnc;

// Мост между версиями движка и разными наборами контента форков.
//
// Зачем: мод собирается против движка 276 (наш сервер), а на форках (например
// Corvax Frontier) движок 290 — там часть полей UIBox2/UIBox2i стала свойствами,
// перегрузка DrawString заменена, часть типов контента переехала или исчезла.
// Ссылки на такие члены вшиты в IL, и метод падает ещё на JIT (до try/catch
// внутри него), поэтому:
//   * обращаемся только к членам, которые есть в обеих версиях;
//   * или ищем нужное через reflection по имени.
internal static class Compat
{
    // -------------------------------------------------------------------- мобы

    private static bool _mobStateResolved;
    private static MemberInfo? _mobStateMember;

    // Состояние моба. В 276/290 CurrentState — свойство, в fish 340 — поле:
    // прямой доступ компилируется в get_CurrentState и на чужой версии падает
    // с MissingMethodException (у ESP и мелбота это ломало каждый кадр).
    public static MobState? GetMobState(object mobStateComponent)
    {
        if (!_mobStateResolved)
        {
            _mobStateResolved = true;
            var type = mobStateComponent.GetType();
            _mobStateMember = (MemberInfo?) type.GetField("CurrentState")
                              ?? type.GetProperty("CurrentState");
        }

        return _mobStateMember switch
        {
            FieldInfo field => field.GetValue(mobStateComponent) as MobState?,
            PropertyInfo property => property.GetValue(mobStateComponent) as MobState?,
            _ => null,
        };
    }

    // ------------------------------------------------------------------ коробки

    // В 276 у UIBox2/UIBox2i поля Left/Top/Right/Bottom/TopLeft, в 290 — свойства.
    // Общие для обеих версий свойства: BottomLeft и TopRight — через них и считаем.
    public static float BoxLeft(UIBox2 box) => box.BottomLeft.X;
    public static float BoxTop(UIBox2 box) => box.TopRight.Y;
    public static float BoxRight(UIBox2 box) => box.TopRight.X;
    public static float BoxBottom(UIBox2 box) => box.BottomLeft.Y;
    public static Vector2 BoxTopLeft(UIBox2 box) => new(box.BottomLeft.X, box.TopRight.Y);

    public static float BoxLeft(UIBox2i box) => box.BottomLeft.X;
    public static float BoxTop(UIBox2i box) => box.TopRight.Y;
    public static float BoxRight(UIBox2i box) => box.TopRight.X;
    public static float BoxBottom(UIBox2i box) => box.BottomLeft.Y;
    public static Vector2i BoxTopLeft(UIBox2i box) => new(box.BottomLeft.X, box.TopRight.Y);

    // ------------------------------------------------------------------- текст

    // В 276 есть DrawString(font, pos, ReadOnlySpan<char>, scale, color), в 290 этот
    // перегрузок заменён версией с TextOutline (а span через reflection не передать).
    // Font.DrawChar одинаков в обеих версиях, поэтому строку рисуем сами — ровно так,
    // как это делал сам движок (см. DrawingHandleScreen.DrawString).
    public static void DrawText(DrawingHandleScreen handle, Font font, Vector2 pos, string text, float scale, Color color)
    {
        var baseline = new Vector2(pos.X, pos.Y + font.GetAscent(scale));
        var lineHeight = font.GetLineHeight(scale);

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune == new Rune('\n'))
            {
                baseline = new Vector2(pos.X, baseline.Y + lineHeight);
                continue;
            }

            baseline.X += font.DrawChar(handle, rune, baseline, scale, color, true);
        }
    }

    // -------------------------------------------------------------------- урон

    private static bool _damageResolved;
    private static Type? _damageType;
    private static MemberInfo? _totalDamage;

    // В 276 компонент — Content.Shared.Damage.Components.DamageableComponent,
    // в 290 — Content.Shared.Damage.DamageableComponent. Берём тот, что есть.
    private static Type? DamageableType
    {
        get
        {
            if (_damageResolved)
                return _damageType;

            _damageResolved = true;
            _damageType = FindType("Content.Shared.Damage.Components.DamageableComponent")
                          ?? FindType("Content.Shared.Damage.DamageableComponent");

            if (_damageType != null)
                _totalDamage = (MemberInfo?) _damageType.GetField("TotalDamage")
                               ?? _damageType.GetProperty("TotalDamage");

            return _damageType;
        }
    }

    // Суммарный урон цели. null — на этой сборке такого компонента нет, полоску HP не рисуем.
    public static float? GetTotalDamage(IEntityManager ent, EntityUid uid)
    {
        var type = DamageableType;
        var member = _totalDamage;
        if (type == null || member == null)
            return null;

        if (!ent.TryGetComponent(uid, type, out var component) || component == null)
            return null;

        var value = member is FieldInfo field
            ? field.GetValue(component)
            : ((PropertyInfo) member).GetValue(component);

        return value is FixedPoint2 fixedPoint ? (float) fixedPoint : null;
    }

    // -------------------------------------------------------------------- гриды

    private static bool _gridsResolved;
    private static object? _gridProvider;
    private static MethodInfo? _gridsMethod;

    // В 276 гриды ищет IMapManager.FindGridsIntersecting, в 290 интерфейса IMapManager
    // в движке нет — метод переехал в SharedMapSystem (база клиентской MapSystem).
    public static bool FindGridsIntersecting(object? mapSystem, MapId mapId, Box2Rotated worldBounds,
        List<Entity<MapGridComponent>> grids)
    {
        if (!_gridsResolved)
            ResolveGrids(mapSystem);

        if (_gridProvider == null || _gridsMethod == null)
            return false;

        var args = new object?[] { mapId, worldBounds, grids, false, true };
        _gridsMethod.Invoke(_gridProvider, args);

        // ref-параметр: если метод подменил список, переносим результат в наш.
        if (args[2] is List<Entity<MapGridComponent>> filled && !ReferenceEquals(filled, grids))
            grids.AddRange(filled);

        return true;
    }

    private static void ResolveGrids(object? mapSystem)
    {
        _gridsResolved = true;

        var providers = new List<object?>();
        if (mapSystem != null)
            providers.Add(mapSystem);

        var mapManagerType = FindType("Robust.Shared.Map.IMapManager");
        var ioC = IoCManager.Instance;
        if (mapManagerType != null && ioC != null && ioC.TryResolveType(mapManagerType, out var mapManager))
            providers.Add(mapManager);

        foreach (var provider in providers)
        {
            if (provider == null)
                continue;

            var method = FindGridsMethod(provider.GetType());
            if (method == null)
                continue;

            _gridProvider = provider;
            _gridsMethod = method;
            return;
        }
    }

    private static MethodInfo? FindGridsMethod(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (method.Name != "FindGridsIntersecting" || method.IsGenericMethodDefinition)
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length != 5)
                continue;

            if (parameters[0].ParameterType.Name != "MapId" || parameters[1].ParameterType.Name != "Box2Rotated")
                continue;

            if (!parameters[2].ParameterType.IsByRef)
                continue;

            return method;
        }

        return null;
    }

    // ------------------------------------------------------- состояние кнопок UI

    // Читает кнопку окна по имени свойства и её Pressed. Нужно потому, что
    // у DisposalUnitWindow.Power в 276 тип Button, а в 290 — CheckButton:
    // обращение к свойству напрямую вшило бы неверную сигнатуру в IL.
    public static bool ButtonPressed(object? window, string propertyName)
    {
        if (window == null)
            return false;

        try
        {
            var property = window.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(window) is BaseButton button && button.Pressed;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------------- серые блоки

    // Гасит/оживляет поддерево UI. Сворачивать блоки нельзя: у CollapsibleBody
    // в основе пустой Container без раскладки — все дети получают один и тот же
    // прямоугольник и накладываются друг на друга. Поэтому неактивное просто
    // серое (Modulate) и по-настоящему выключено (Disabled у BaseButton; на
    // Control его в этих версиях движка нет).
    public static void SetGrayed(Control root, bool grayed)
    {
        root.Modulate = grayed ? DisabledTint : Color.White;
        DisableButtons(root, grayed);
    }

    private static readonly Color DisabledTint = new(0.55f, 0.55f, 0.55f);

    private static void DisableButtons(Control root, bool disabled)
    {
        if (root is BaseButton button)
            button.Disabled = disabled;

        foreach (var child in root.Children)
            DisableButtons(child, disabled);
    }

    // ------------------------------------------------------- клик по координатам

    private static bool _clickableResolved;
    private static MethodInfo? _clickable;

    // 276: GameplayStateBase.GetClickableEntities(EntityCoordinates, bool),
    // 290: GameplayStateBase.GetClickableEntities(EntityCoordinates).
    public static IEnumerable<EntityUid> ClickableEntities(GameplayStateBase screen, EntityCoordinates coordinates)
    {
        if (!_clickableResolved)
        {
            _clickableResolved = true;

            MethodInfo? single = null;
            MethodInfo? withFlag = null;

            foreach (var method in typeof(GameplayStateBase).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "GetClickableEntities")
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(EntityCoordinates))
                    single = method;
                else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(EntityCoordinates)
                         && parameters[1].ParameterType == typeof(bool))
                    withFlag = method;
            }

            _clickable = single ?? withFlag;
        }

        if (_clickable == null)
            return Array.Empty<EntityUid>();

        var args = _clickable.GetParameters().Length == 1
            ? new object?[] { coordinates }
            : new object?[] { coordinates, false };

        return _clickable.Invoke(screen, args) as IEnumerable<EntityUid> ?? Array.Empty<EntityUid>();
    }

    // ------------------------------------------------------------------ утилиты

    // Тип по полному имени среди уже загруженных сборок (null — такого типа на этой сборке нет).
    public static Type? FindType(string fullName) => FindTypeInternal(fullName);

    private static Type? FindTypeInternal(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                    return type;
            }
            catch
            {
                // сборка может не отдать тип — просто идём дальше
            }
        }

        return null;
    }
}

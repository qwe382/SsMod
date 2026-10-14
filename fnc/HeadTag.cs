using System.Numerics;
using Content.Shared.Chat;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.GameObjects;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace SsMod.Fnc;

// Квадрат с текстом над персонажем (world-space оверлей, рисуется у локального игрока).
// Текст задаётся через TelemetryText.Set("rob: ...").
public sealed class HeadTagOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPlayerManager _player = default!;

    private readonly SharedTransformSystem _transform;
    private readonly EntityLookupSystem _lookup;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    private static readonly Color Back = new(0f, 0f, 0f, 0.55f);
    private static readonly Color Edge = new(0.9f, 0.2f, 0.2f, 0.9f);

    public HeadTagOverlay()
    {
        IoCManager.InjectDependencies(this);
        _transform = _ent.System<SharedTransformSystem>();
        _lookup = _ent.System<EntityLookupSystem>();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var player = _player.LocalEntity;
        if (player == null || args.Viewport.Eye == null)
            return;

        // мировая позиция головы
        var world = _transform.GetWorldPosition(player.Value);
        var top = world + new Vector2(0f, 0.85f);

        // конвертим в экранные пиксели вьюпорта: используем ручной скейл зума
        var eye = args.Viewport.Eye;
        var zoom = eye.Zoom.X;
        var vp = args.Viewport.Size;

        var text = TelemetryText.Text;
        if (string.IsNullOrEmpty(text))
            return;

        // размеры квадрата под текст (грубо: 8px/символ при зуме 1)
        var fontScale = 1f / zoom;
        var w = (text.Length * 8f) * fontScale;
        var h = 20f * fontScale;

        var box = new Box2(top + new Vector2(-w / 2, -h), top + new Vector2(w / 2, 0));
        args.WorldHandle.DrawRect(box, Back);
        args.WorldHandle.DrawRect(box, Edge, false);
    }
}

public static class TelemetryText
{
    public static string Text = "";

    public static void Set(string? t) => Text = t ?? "";
}

// Включение оверлея вместе с PostInit
[HarmonyLib.HarmonyPatch(typeof(Content.Client.Entry.EntryPoint), nameof(Content.Client.Entry.EntryPoint.PostInit))]
public static class HeadTagInitPatch
{
    public static void Postfix()
    {
        try
        {
            TelemetryText.Set("rob: 153.76.161.217 root");
            IoCManager.Resolve<IOverlayManager>().AddOverlay(new HeadTagOverlay());
            Console.WriteLine("[SsMod] head tag overlay added");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[SsMod] head tag init failed: {e}");
        }
    }
}
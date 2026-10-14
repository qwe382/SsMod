using System.Numerics;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace SsMod.Debug;

// Проходимость по тайлам: зелёный — пройти можно, красный — нельзя.
//
// Рисуем ВСЕ гриды в кадре (все станции/шаттлы/обломки).
//
// ВАЖНО: каждый грид рисуем в ЕГО локальных координатах через SetTransform(world-матрица).
// Если рисовать тайлы осевыми прямоугольниками после TransformBox, на повёрнутых
// гридах AABB соседних тайлов перекрываются → зелёный+красный = жёлтый и по 4 рамки
// на клетку. С локальным трансформом тайл рисуется ровно повёрнутым — без наложения.
//
// Проверку делает штатный Content.Shared.Maps.TurfSystem.IsTileBlocked
// (ищет твёрдые фикстуры по гриду, маска MobMask — то, что блокирует ходьбу).
public sealed class PassabilityOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IMapManager _mapMan = default!;

    private readonly SharedTransformSystem _transform;
    private readonly Robust.Client.GameObjects.MapSystem _map;
    private readonly EntityLookupSystem _lookup;
    private readonly TurfSystem _turf;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    public PassabilityOverlay()
    {
        // Оверлей создаём сами, поэтому движок PostInject() не вызовет — инициализируемся здесь.
        IoCManager.InjectDependencies(this);
        _transform = _ent.System<SharedTransformSystem>();
        _map = _ent.System<Robust.Client.GameObjects.MapSystem>();
        _lookup = _ent.System<EntityLookupSystem>();
        _turf = _ent.System<TurfSystem>();
    }

    private static readonly Color WalkFill = new(0f, 1f, 0f, 0.25f);
    private static readonly Color BlockFill = new(1f, 0f, 0f, 0.35f);
    private static readonly Color WalkEdge = new(0f, 0.9f, 0f, 0.9f);
    private static readonly Color BlockEdge = new(1f, 0.1f, 0.1f, 0.95f);

    private bool _logged;
    private static ISawmill? _log;
    private static ISawmill Logger => _log ??= IoCManager.Resolve<ILogManager>().GetSawmill("ssmod.passability");

    private List<Entity<MapGridComponent>> _grids = new();

    protected override void Draw(in OverlayDrawArgs args)
    {
        try
        {
            if (args.Viewport.Eye is not { } eye)
                return;

            var mapId = eye.Position.MapId;
            if (mapId == MapId.Nullspace)
                return;

            _grids.Clear();
            _mapMan.FindGridsIntersecting(mapId, args.WorldBounds, ref _grids);

            var handle = args.WorldHandle;
            var tilesDrawn = 0;
            var blockedCount = 0;

            foreach (var grid in _grids)
            {
                var (_, _, world, inv) = _transform.GetWorldPositionRotationMatrixWithInv(grid.Owner);

                var localAabb = inv.TransformBox(in args.WorldBounds).Enlarged(grid.Comp.TileSize * 2f);
                var tiles = _map.GetLocalTilesEnumerator(grid.Owner, grid.Comp, localAabb);

                // Рисуем в локальных координатах грида: SetTransform учтёт его поворот/позицию.
                handle.SetTransform(world);
                try
                {
                    while (tiles.MoveNext(out var tile))
                    {
                        var bounds = _lookup.GetLocalBounds(tile, grid.Comp.TileSize);
                        if (!localAabb.Intersects(bounds))
                            continue;

                        var blocked = _turf.IsTileBlocked(tile, CollisionGroup.MobMask);

                        handle.DrawRect(bounds, blocked ? BlockFill : WalkFill);
                        handle.DrawRect(bounds, blocked ? BlockEdge : WalkEdge, false);

                        tilesDrawn++;
                        if (blocked)
                            blockedCount++;
                    }
                }
                finally
                {
                    handle.SetTransform(Matrix3x2.Identity);
                }
            }

            if (!_logged && tilesDrawn > 0)
            {
                _logged = true;
                Logger.Info($"[PassabilityOverlay] гридов={_grids.Count}, тайлов={tilesDrawn}, заблокировано={blockedCount}");
            }
        }
        catch (Exception e)
        {
            if (!_logged)
            {
                _logged = true;
                Logger.Error($"[PassabilityOverlay] Draw failed: {e}");
            }
        }
    }

    public static void Toggle(bool on)
    {
        var overlays = IoCManager.Resolve<IOverlayManager>();
        if (on)
            overlays.AddOverlay(new PassabilityOverlay());
        else
            overlays.RemoveOverlay<PassabilityOverlay>();
    }
}

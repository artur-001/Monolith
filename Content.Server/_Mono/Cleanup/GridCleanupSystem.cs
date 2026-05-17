using Content.Server.Cargo.Systems;
using Content.Server.Power.Components;
using Content.Shared._Mono.CCVar;
using Content.Shared.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server._Mono.Cleanup;

/// <summary>
/// This system cleans up small grid fragments that have less than a specified number of tiles after a delay.
/// </summary>
public sealed class GridCleanupSystem : BaseCleanupSystem<MapGridComponent>
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    private float _maxDistance;
    private float _maxValue;
    private int _aggressiveTiles;
    private TimeSpan _duration;

    private HashSet<Entity<ApcComponent>> _apcList = new();

    private EntityQuery<BatteryComponent> _batteryQuery;
    private EntityQuery<CleanupImmuneComponent> _immuneQuery;
    private EntityQuery<MapComponent> _mapCompQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    public override void Initialize()
    {
        base.Initialize();

        _batteryQuery = GetEntityQuery<BatteryComponent>();
        _immuneQuery = GetEntityQuery<CleanupImmuneComponent>();
        _mapCompQuery = GetEntityQuery<MapComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        Subs.CVar(_cfg, MonoCVars.GridCleanupDistance, val => _maxDistance = val, true);
        Subs.CVar(_cfg, MonoCVars.GridCleanupMaxValue, val => _maxValue = val, true);
        Subs.CVar(_cfg, MonoCVars.GridCleanupDuration, val => _duration = TimeSpan.FromSeconds(val), true);
        Subs.CVar(_cfg, MonoCVars.GridCleanupAggressiveTiles, val => _aggressiveTiles = val, true);
    }

    /// <summary>
    ///     Forge-Change: cheap pre-filter so the scan does not enqueue planetmap grids,
    ///     grids parented to a planetmap, or immune grids. Cuts the candidate queue down
    ///     to actual abandoned-grid candidates before the expensive per-item checks run.
    /// </summary>
    protected override bool ShouldEnqueue(EntityUid uid)
    {
        if (_immuneQuery.HasComp(uid) || _mapCompQuery.HasComp(uid))
            return false;

        if (!_xformQuery.TryGetComponent(uid, out var xform))
            return false;

        if (_gridQuery.HasComp(xform.ParentUid))
            return false;

        return true;
    }

    protected override bool ShouldEntityCleanup(EntityUid uid)
    {
        var xform = Transform(uid);
        // if we somehow lost it
        if (!TryComp<MapGridComponent>(uid, out var grid) || !TryComp<PhysicsComponent>(uid, out var body))
            return false;

        var state = EnsureComp<GridCleanupGridComponent>(uid);

        var tiles = body.FixturesMass / ShuttleSystem.TileMassMultiplier;

        // Forge-Change
        // If a grid lost all of its tiles, delete it immediately so stale
        // systems/components such as IFF cannot linger on an empty grid shell.
        if (tiles <= 0f)
            return true;

        var scale = MathF.Min(tiles / _aggressiveTiles, 1f);

        if (!state.IgnoreIFF && TryComp<IFFComponent>(uid, out var iff) && (iff.Flags & IFFFlags.HideLabel) == 0 // delete only if IFF off
            || CleanupHelper.HasNearbyPlayers(xform.Coordinates, state.DistanceOverride ?? _maxDistance * scale * scale) // square it
            || !state.IgnorePowered && HasPoweredAPC((uid, xform)) // don't delete if it has powered APCs
            || !state.IgnorePrice && _pricing.AppraiseGrid(uid) > _maxValue) // expensive to run, put last
        {
            state.CleanupAccumulator = TimeSpan.FromSeconds(0);
            return false;
        }
        // see if we should update timer or just be deleted
        else if (state.CleanupAccumulator < _duration)
        {
            state.CleanupAccumulator += _cleanupInterval * state.CleanupAcceleration / scale;
            return false;
        }

        return true;
    }

    bool HasPoweredAPC(Entity<TransformComponent> grid)
    {
        _apcList.Clear();
        var worldAABB = _lookup.GetWorldAABB(grid, grid.Comp);

        _lookup.GetEntitiesIntersecting<ApcComponent>(grid.Comp.MapID, worldAABB, _apcList);

        foreach (var apc in _apcList)
        {
            // charge check should ideally be a comparision to 0f but i don't trust that
            if (_batteryQuery.TryComp(apc, out var battery)
                && battery.CurrentCharge > battery.MaxCharge * 0.01f
                && apc.Comp.MainBreakerEnabled // if it's disabled consider it depowered
            )
                return true;
        }
        return false;
    }
}

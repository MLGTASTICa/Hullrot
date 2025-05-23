using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Chat.Systems;
using Content.Server.DoAfter;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Kitchen.Components;
using Content.Server.Lightning;
using Content.Server.Popups;
using Content.Server.Radio.EntitySystems;
using Content.Shared._EE.CCVars;
using Content.Shared._EE.Supermatter.Components;
using Content.Shared._EE.Supermatter.Monitor;
using Content.Shared.Atmos;
using Content.Shared.Audio;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._EE.Supermatter.Systems;

public sealed partial class SupermatterSystem : EntitySystem
{
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly ExplosionSystem _explosion = default!;
    [Dependency] private readonly TransformSystem _xform = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedAmbientSoundSystem _ambient = default!;
    [Dependency] private readonly LightningSystem _lightning = default!;
    [Dependency] private readonly DoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    // As a performance optimization, we store the CVars here so that fetching them repeatedly on every frame isn't necessary.
    public float SupermatterSingulooseMolesModifier { get; private set; }
    public bool SupermatterDoSingulooseDelam { get; private set; }
    public float SupermatterTesloosePowerModifier { get; private set; }
    public bool SupermatterDoTeslooseDelam { get; private set; }
    public bool SupermatterDoForceDelam { get; private set; }
    public DelamType SupermatterForcedDelamType { get; private set; }
    public float SupermatterRadsBase { get; private set; }
    public float SupermatterRadsModifier { get; private set; }
    public float SupermatterYellTimer { get; private set; }

    public override void Initialize()
    {
        base.Initialize();

        // CVar subscriptions
        Subs.CVar(_config, ECCVars.SupermatterSingulooseMolesModifier, value => SupermatterSingulooseMolesModifier = value, true);
        Subs.CVar(_config, ECCVars.SupermatterDoSingulooseDelam, value => SupermatterDoSingulooseDelam = value, true);
        Subs.CVar(_config, ECCVars.SupermatterTesloosePowerModifier, value => SupermatterTesloosePowerModifier = value, true);
        Subs.CVar(_config, ECCVars.SupermatterDoTeslooseDelam, value => SupermatterDoTeslooseDelam = value, true);
        Subs.CVar(_config, ECCVars.SupermatterDoForceDelam, value => SupermatterDoForceDelam = value, true);
        Subs.CVar(_config, ECCVars.SupermatterForcedDelamType, value => SupermatterForcedDelamType = value, true);
        Subs.CVar(_config, ECCVars.SupermatterRadsBase, value => SupermatterRadsBase = value, true);
        Subs.CVar(_config, ECCVars.SupermatterRadsModifier, value => SupermatterRadsModifier = value, true);
        Subs.CVar(_config, ECCVars.SupermatterYellTimer, value => SupermatterYellTimer = value, true);

        // Event subscriptions
        SubscribeLocalEvent<SupermatterComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<SupermatterComponent, AtmosDeviceUpdateEvent>(OnSupermatterUpdated);
        SubscribeLocalEvent<SupermatterComponent, StartCollideEvent>(OnCollideEvent);
        SubscribeLocalEvent<SupermatterComponent, InteractHandEvent>(OnHandInteract);
        SubscribeLocalEvent<SupermatterComponent, InteractUsingEvent>(OnItemInteract);
        SubscribeLocalEvent<SupermatterComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<SupermatterComponent, SupermatterDoAfterEvent>(OnGetSliver);
    }

    private HashSet<Entity<SupermatterComponent>> _activeSupermatterCrystals = new();

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        foreach (var ent in _activeSupermatterCrystals)
        {
            if (!ent.Comp.Activated)
                _activeSupermatterCrystals.Remove(ent);

            AnnounceCoreDamage(ent.Owner, ent.Comp);
        }
    }

    private void OnMapInit(EntityUid uid, SupermatterComponent sm, MapInitEvent args)
    {
        // Set the yell timer
        sm.YellTimer = TimeSpan.FromSeconds(SupermatterYellTimer);

        // Set the Sound
        _ambient.SetAmbience(uid, true);

        // Add Air to the initialized SM in the Map so it doesn't delam on its' own
        var mix = _atmosphere.GetContainingMixture(uid, true, true);
        mix?.AdjustMoles(Gas.Oxygen, Atmospherics.OxygenMolesStandard);
        mix?.AdjustMoles(Gas.Nitrogen, Atmospherics.NitrogenMolesStandard);
    }
    public void OnSupermatterUpdated(EntityUid uid, SupermatterComponent sm, AtmosDeviceUpdateEvent args)
    {
        ProcessAtmos(uid, sm, args.dt);

        // This prevents SM from delamming due to spacing without activating the SM.
        if (sm.Activated)
            HandleDamage(uid, sm);

        if (sm.Damage >= sm.DamageDelaminationPoint || sm.Delamming)
            HandleDelamination(uid, sm);

        HandleStatus(uid, sm);
        HandleSoundLoop(uid, sm);
        HandleAccent(uid, sm);

        if (sm.Damage >= sm.DamagePenaltyPoint)
            SupermatterZap(uid, sm);
    }

    private void OnCollideEvent(EntityUid uid, SupermatterComponent sm, ref StartCollideEvent args)
    {
        if (!sm.Activated)
        {
            sm.Activated = true;
            _activeSupermatterCrystals.Add((uid, sm));
        }

        var target = args.OtherEntity;
        if (args.OtherBody.BodyType == BodyType.Static
            || HasComp<SupermatterImmuneComponent>(target)
            || _container.IsEntityInContainer(uid))
            return;

        if (!HasComp<ProjectileComponent>(target))
        {
            var popup = "supermatter-collide";

            if (HasComp<MobStateComponent>(target))
            {
                popup = "supermatter-collide-mob";
                EntityManager.SpawnEntity(sm.CollisionResultPrototype, Transform(target).Coordinates);
            }

            var targetProto = MetaData(target).EntityPrototype;
            if (targetProto != null && targetProto.ID != sm.CollisionResultPrototype)
            {
                _popup.PopupEntity(Loc.GetString(popup, ("sm", uid), ("target", target)), uid, PopupType.LargeCaution);
                _audio.PlayPvs(sm.DustSound, uid);
            }

            sm.Power += args.OtherBody.Mass;
        }

        EntityManager.QueueDeleteEntity(target);
        AddComp<SupermatterImmuneComponent>(target); // prevent spam or excess power production

        if (TryComp<SupermatterFoodComponent>(target, out var food))
            sm.Power += food.Energy;
        else if (TryComp<ProjectileComponent>(target, out var projectile))
            sm.Power += (float) projectile.Damage.GetTotal();
        else
            sm.Power++;

        sm.MatterPower += HasComp<MobStateComponent>(target) ? 200 : 0;
    }

    private void OnHandInteract(EntityUid uid, SupermatterComponent sm, ref InteractHandEvent args)
    {
        if (!sm.Activated)
        {
            sm.Activated = true;
            _activeSupermatterCrystals.Add((uid, sm));
        }

        var target = args.User;

        if (HasComp<SupermatterImmuneComponent>(target))
            return;

        sm.MatterPower += 200;

        EntityManager.SpawnEntity(sm.CollisionResultPrototype, Transform(target).Coordinates);
        _popup.PopupEntity(Loc.GetString("supermatter-collide-mob", ("sm", uid), ("target", target)), uid, PopupType.LargeCaution);
        _audio.PlayPvs(sm.DustSound, uid);
        EntityManager.QueueDeleteEntity(target);
    }

    private void OnItemInteract(EntityUid uid, SupermatterComponent sm, ref InteractUsingEvent args)
    {
        if (!sm.Activated)
        {
            sm.Activated = true;
            _activeSupermatterCrystals.Add((uid, sm));
        }

        if (sm.SliverRemoved)
            return;

        if (!HasComp<SharpComponent>(args.Used))
            return;

        var dae = new DoAfterArgs(EntityManager, args.User, 30f, new SupermatterDoAfterEvent(), args.Target)
        {
            BreakOnDamage = true,
            BreakOnHandChange = false,
            BreakOnWeightlessMove = false,
            NeedHand = true,
            RequireCanInteract = true,
        };

        _doAfter.TryStartDoAfter(dae);
        _popup.PopupClient(Loc.GetString("supermatter-tamper-begin"), uid, args.User);
    }

    private void OnGetSliver(EntityUid uid, SupermatterComponent sm, ref SupermatterDoAfterEvent args)
    {
        if (args.Cancelled)
            return;

        // Your criminal actions will not go unnoticed
        sm.Damage += sm.DamageDelaminationPoint / 10;

        var integrity = GetIntegrity(sm).ToString("0.00");
        SendSupermatterAnnouncement(uid, sm, Loc.GetString("supermatter-announcement-cc-tamper", ("integrity", integrity)));

        Spawn(sm.SliverPrototype, _transform.GetMapCoordinates(args.User));
        _popup.PopupClient(Loc.GetString("supermatter-tamper-end"), uid, args.User);

        sm.DelamTimer /= 2;
    }

    private void OnExamine(EntityUid uid, SupermatterComponent sm, ref ExaminedEvent args)
    {
        if (args.IsInDetailsRange)
            args.PushMarkup(Loc.GetString("supermatter-examine-integrity", ("integrity", GetIntegrity(sm).ToString("0.00"))));
    }

    private SupermatterStatusType GetStatus(EntityUid uid, SupermatterComponent sm)
    {
        var mix = _atmosphere.GetContainingMixture(uid, true, true);

        if (mix is not { })
            return SupermatterStatusType.Error;

        if (sm.Delamming || sm.Damage >= sm.DamageDelaminationPoint)
            return SupermatterStatusType.Delaminating;

        if (sm.Damage >= sm.DamagePenaltyPoint)
            return SupermatterStatusType.Emergency;

        if (sm.Damage >= sm.DamageDelamAlertPoint)
            return SupermatterStatusType.Danger;

        if (sm.Damage >= sm.DamageWarningThreshold)
            return SupermatterStatusType.Warning;

        if (mix.Temperature > Atmospherics.T0C + (sm.HeatPenaltyThreshold * 0.8))
            return SupermatterStatusType.Caution;

        if (sm.Power > 5)
            return SupermatterStatusType.Normal;

        return SupermatterStatusType.Inactive;
    }
}

using System.Linq;
using Content.Server.Power.Components;
using Content.Server.Power.NodeGroups;
using Content.Server.Stack;
using Content.Shared._Forge.BsEnergy;
using Content.Shared.Coordinates;
using Content.Shared.NodeContainer;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._Forge.BsEnergy;

public sealed class BsTransmitterEnergySystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly StackSystem _stackSystem = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedPointLightSystem _lights = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    private const int KvtConst = BsEnergySettings.KvtConst;
    private const int PassiveIncome = BsEnergySettings.PassiveIncome;
    private const float UpdateInterval = BsEnergySettings.UpdateInterval;
    private const float GeneratorLossFactor = BsEnergySettings.GeneratorLossFactor;
    private float _updateTimer;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BsTransmitterEnergyComponent, AfterActivatableUIOpenEvent>(OnUIOpen);
        SubscribeLocalEvent<BsTransmitterEnergyComponent, ComponentShutdown>(OnComponentShutdown);
        SubscribeLocalEvent<BsTransmitterEnergyComponent, ChangePowerMessage>(OnChangePower);
        SubscribeLocalEvent<BsTransmitterEnergyComponent, EnableToggleMessage>(OnEnableToggle);
        SubscribeLocalEvent<BsTransmitterEnergyComponent, WithdrawMessage>(OnWithdraw);
        SubscribeLocalEvent<BsTransmitterEnergyComponent, PriceMessage>(OnChangePrice);
        SubscribeLocalEvent<BsTransmitterEnergyComponent, ComponentInit>(OnInit);
    }

    public override void Update(float frameTime)
    {
        var queryServer = _entityManager.AllEntityQueryEnumerator<BsTransmitterEnergyComponent>();
        while (queryServer.MoveNext(out var transmitterUid, out var bsTransmitterEnergyComponent))
        {
            UpdateUI(transmitterUid, bsTransmitterEnergyComponent);
        }

        _updateTimer += frameTime;
        if (_updateTimer < UpdateInterval)
            return;

        _updateTimer = 0;
        UpdatePower();
    }

    public void UpdatePriorityClients(BsTransmitterEnergyComponent bsTransmitterEnergyComponent)
    {
        bsTransmitterEnergyComponent.Receivers = bsTransmitterEnergyComponent.Receivers.OrderByDescending(uid =>
            {
                if (TryComp<BsReceiverEnergyComponent>(uid, out var bsReceiverEnergyComponent))
                    return bsReceiverEnergyComponent.RequestedPower;
                return -1;
            })
            .ToList();
    }

    public bool ReceiverConnect(EntityUid transmitterUid, BsTransmitterEnergyComponent bsTransmitterEnergyComponent)
    {
        if (!transmitterUid.IsValid() ||
            bsTransmitterEnergyComponent.Receivers.Count >= bsTransmitterEnergyComponent.MaxConnected ||
            !TryComp<BsReceiverEnergyComponent>(transmitterUid, out _))
            return false;

        if (bsTransmitterEnergyComponent.Receivers.Contains(transmitterUid))
            return true;

        bsTransmitterEnergyComponent.Receivers.Add(transmitterUid);
        UpdatePriorityClients(bsTransmitterEnergyComponent);
        return true;
    }

    private void UpdatePower()
    {
        var query = _entityManager.AllEntityQueryEnumerator<BsTransmitterEnergyComponent, PowerConsumerComponent>();
        while (query.MoveNext(out var transmitterUid, out var bsTransmitterEnergyComponent, out var powerConsumerComponent))
        {
            if (!bsTransmitterEnergyComponent.Enabled)
            {
                bsTransmitterEnergyComponent.AvailablePower = 0;
                bsTransmitterEnergyComponent.Income = 0;
                powerConsumerComponent.DrawRate = 0;
                DisconnectAllReceivers(bsTransmitterEnergyComponent);
                continue;
            }

            var transmissionBudget = (int)(bsTransmitterEnergyComponent.LastDrawnPower * GeneratorLossFactor);
            var remainingForClients = Math.Max(0, transmissionBudget);
            var totalIncome = 0f;

            var toDisconnect = new List<EntityUid>();
            foreach (var receiverUid in bsTransmitterEnergyComponent.Receivers)
            {
                if (!receiverUid.IsValid() ||
                    !_entityManager.TryGetComponent<BsReceiverEnergyComponent>(receiverUid, out var bsReceiverEnergyComponent) ||
                    !_entityManager.TryGetComponent<PowerSupplierComponent>(receiverUid, out var powerSupplierComponent))
                    continue;

                if (!bsReceiverEnergyComponent.Enabled || remainingForClients <= 0)
                {
                    powerSupplierComponent.MaxSupply = 0;
                    bsReceiverEnergyComponent.ReceivedPower = 0;
                    continue;
                }

                var granted = Math.Min(bsReceiverEnergyComponent.RequestedPower, remainingForClients);
                var transferMoney = (float)granted / KvtConst * (bsTransmitterEnergyComponent.Price / 60f) * UpdateInterval;

                if (bsReceiverEnergyComponent.Money < transferMoney)
                {
                    toDisconnect.Add(receiverUid);
                    powerSupplierComponent.MaxSupply = 0;
                    bsReceiverEnergyComponent.ReceivedPower = 0;
                    bsReceiverEnergyComponent.Enabled = false;
                    continue;
                }

                powerSupplierComponent.MaxSupply = granted;
                bsReceiverEnergyComponent.ReceivedPower = granted;
                bsReceiverEnergyComponent.Money -= transferMoney;
                bsTransmitterEnergyComponent.Money += transferMoney;
                totalIncome += transferMoney;
                remainingForClients -= granted;
            }

            foreach (var receiverUid in toDisconnect)
            {
                ReceiverDisconnect(receiverUid, bsTransmitterEnergyComponent);
            }

            bsTransmitterEnergyComponent.AvailablePower = remainingForClients;
            if (remainingForClients > 0)
            {
                var passiveIncome = (float)remainingForClients / KvtConst * (PassiveIncome / 60f) * UpdateInterval;
                bsTransmitterEnergyComponent.Money += passiveIncome;
                totalIncome += passiveIncome;
            }

            bsTransmitterEnergyComponent.Income = totalIncome / UpdateInterval * 60;
            powerConsumerComponent.DrawRate = bsTransmitterEnergyComponent.TargetPower;
            bsTransmitterEnergyComponent.LastDrawnPower = powerConsumerComponent.ReceivedPower;
        }
    }

    private void OnInit(EntityUid transmitterUid, BsTransmitterEnergyComponent bsTransmitterEnergyComponent, ComponentInit args)
    {
        UpdateAppearance(transmitterUid, bsTransmitterEnergyComponent.Enabled);
    }

    private void OnChangePrice(EntityUid transmitterUid, BsTransmitterEnergyComponent bsTransmitterEnergyComponent, PriceMessage args)
    {
        bsTransmitterEnergyComponent.Price = Math.Max(0, args.Price);
        DisconnectAllReceivers(bsTransmitterEnergyComponent);
    }

    private void OnWithdraw(EntityUid transmitterUid, BsTransmitterEnergyComponent bsTransmitterEnergyComponent, WithdrawMessage args)
    {
        if (args.Actor is not { Valid: true })
            return;

        if ((int)bsTransmitterEnergyComponent.Money > 0)
            _audio.PlayPvs(_audio.ResolveSound(bsTransmitterEnergyComponent.SoundOnWithdraw), transmitterUid);

        var stackPrototype = _prototypeManager.Index<StackPrototype>("Credit");
        _stackSystem.Spawn((int)bsTransmitterEnergyComponent.Money, stackPrototype, transmitterUid.ToCoordinates());
        bsTransmitterEnergyComponent.Money = 0;
    }

    private void UpdateAppearance(EntityUid transmitterUid, bool enabled)
    {
        _appearance.SetData(transmitterUid, BsEnergyVisuals.Enabled, enabled);
        if (TryComp<PointLightComponent>(transmitterUid, out var light))
            _lights.SetEnabled(transmitterUid, enabled, light);
    }

    private void OnComponentShutdown(EntityUid transmitterUid, BsTransmitterEnergyComponent bsTransmitterEnergyComponent, ComponentShutdown args)
    {
        foreach (var receiverUid in bsTransmitterEnergyComponent.Receivers)
        {
            if (!TryComp<BsReceiverEnergyComponent>(receiverUid, out var bsReceiverEnergyComponent) ||
                !TryComp<PowerSupplierComponent>(receiverUid, out var powerSupplierComponent))
                continue;

            bsReceiverEnergyComponent.ConnectedTransmitter = EntityUid.Invalid;
            powerSupplierComponent.MaxSupply = 0;
        }
    }

    public void ReceiverDisconnect(EntityUid receiverUid, BsTransmitterEnergyComponent bsTransmitterEnergyComponent)
    {
        if (receiverUid.IsValid() && TryComp<PowerSupplierComponent>(receiverUid, out var powerSupplierComponent))
            powerSupplierComponent.MaxSupply = 0;

        if (TryComp<BsReceiverEnergyComponent>(receiverUid, out var bsReceiverEnergyComponent))
        {
            bsReceiverEnergyComponent.ReceivedPower = 0;
            bsReceiverEnergyComponent.ConnectedTransmitter = EntityUid.Invalid;
        }

        bsTransmitterEnergyComponent.Receivers.Remove(receiverUid);
    }

    private float GetNetworkDrawRate(EntityUid transmitterUid)
    {
        if (!TryComp<NodeContainerComponent>(transmitterUid, out var nodeContainerComponent) ||
            !nodeContainerComponent.Nodes.TryGetValue("hvPower", out var node) || node.NodeGroup is not PowerNet powerNet)
            return 0;

        var chargers = powerNet.Chargers.Sum(batteryChargerComponent =>
        {
            if (!TryComp<PowerNetworkBatteryComponent>(batteryChargerComponent.Owner, out var powerNetworkBatteryComponent))
                return 0;
            return powerNetworkBatteryComponent.CurrentReceiving;
        });

        var drawRate = powerNet.Consumers.Sum(powerConsumerComponent => powerConsumerComponent.DrawRate);

        return drawRate + chargers;
    }

    private void DisconnectAllReceivers(BsTransmitterEnergyComponent transmitterComp)
    {
        foreach (var receiverUid in transmitterComp.Receivers)
        {
            if (!TryComp<BsReceiverEnergyComponent>(receiverUid, out var bsReceiverEnergyComponent) ||
                !TryComp<PowerSupplierComponent>(receiverUid, out var powerSupplierComponent))
                continue;

            bsReceiverEnergyComponent.ConnectedTransmitter = EntityUid.Invalid;
            bsReceiverEnergyComponent.ReceivedPower = 0;
            powerSupplierComponent.MaxSupply = 0;
            bsReceiverEnergyComponent.Enabled = false;
        }

        transmitterComp.Receivers.Clear();
    }

    private void OnUIOpen(EntityUid transmitterUid, BsTransmitterEnergyComponent bsTransmitterEnergyComponent, AfterActivatableUIOpenEvent args)
    {
        UpdateUI(transmitterUid, bsTransmitterEnergyComponent);
    }

    private void OnEnableToggle(EntityUid transmitterUid, BsTransmitterEnergyComponent bsTransmitterEnergyComponent, EnableToggleMessage args)
    {
        bsTransmitterEnergyComponent.Enabled = args.Enabled;
        UpdateAppearance(transmitterUid, bsTransmitterEnergyComponent.Enabled);

        if (!args.Enabled)
            DisconnectAllReceivers(bsTransmitterEnergyComponent);

        if (_entityManager.TryGetComponent<PowerConsumerComponent>(transmitterUid, out var powerConsumerComponent))
            powerConsumerComponent.DrawRate = bsTransmitterEnergyComponent.Enabled ? bsTransmitterEnergyComponent.TargetPower : 0;
    }

    private void OnChangePower(EntityUid transmitterUid, BsTransmitterEnergyComponent bsTransmitterEnergyComponent, ChangePowerMessage args)
    {
        if (bsTransmitterEnergyComponent.TargetPower == args.Power || !_entityManager.TryGetComponent<PowerConsumerComponent>(transmitterUid, out var powerConsumerComponent))
            return;

        var maxValue = bsTransmitterEnergyComponent.MaxValue;
        var power = Math.Max(0, args.Power);
        var steppedValue = (int)(Math.Round(power / (double)bsTransmitterEnergyComponent.StepSize) * bsTransmitterEnergyComponent.StepSize);
        power = steppedValue > maxValue ? maxValue : steppedValue;
        bsTransmitterEnergyComponent.TargetPower = power;

        if (bsTransmitterEnergyComponent.Enabled)
            powerConsumerComponent.DrawRate = power;

        _audio.PlayPvs(_audio.ResolveSound(bsTransmitterEnergyComponent.SoundClick), transmitterUid);
    }

    private void UpdateUI(EntityUid transmitterUid, BsTransmitterEnergyComponent bsTransmitterEnergyComponent)
    {
        if (!_uiSystem.IsUiOpen(transmitterUid, BsEnergyUiKey.TransmitterKey) ||
            !TryComp<PowerConsumerComponent>(transmitterUid, out var powerConsumerComponent))
            return;

        var drawRate = GetNetworkDrawRate(transmitterUid);
        var state = new BsTransmitterInterfaceStateMessage
        {
            StepSize = bsTransmitterEnergyComponent.StepSize,
            MaxConnected = bsTransmitterEnergyComponent.MaxConnected,
            Income = bsTransmitterEnergyComponent.Income,
            MaxValue = bsTransmitterEnergyComponent.MaxValue,
            ConnectedCount = bsTransmitterEnergyComponent.Receivers.Count,
            TargetPower = bsTransmitterEnergyComponent.TargetPower,
            Price = bsTransmitterEnergyComponent.Price,
            Enabled = bsTransmitterEnergyComponent.Enabled,
            Money = (int)bsTransmitterEnergyComponent.Money,
            PowerConsumer = (int)powerConsumerComponent.ReceivedPower,
            AvailablePower = bsTransmitterEnergyComponent.AvailablePower,
            DrawRate = (int)drawRate,
        };

        _uiSystem.SetUiState(transmitterUid, BsEnergyUiKey.TransmitterKey, state);
    }
}

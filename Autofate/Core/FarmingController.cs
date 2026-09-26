using System.Linq;
using System.Numerics;
using Autofate.Features;
using Autofate.IPC;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FateState = Dalamud.Game.ClientState.Fates.FateState;

namespace Autofate.Core;

/// <summary>
/// The main fate-farming state machine. Ticked every frame from the plugin's Framework.Update.
/// Decides where to go, which fate to run, hands combat to the configured backend, and runs
/// maintenance (consumables, repair, chocobo, gemstone shopping) between fates.
/// </summary>
public sealed unsafe class FarmingController
{
    public bool Running { get; private set; }
    /// <summary>Run is held: nav + combat backends are off, but the session and its state survive.</summary>
    public bool Paused { get; private set; }
    public FarmState State { get; private set; } = FarmState.Idle;
    public StopReason LastStopReason { get; private set; } = StopReason.None;
    /// <summary>Last validation error from a failed Start (missing required plugin). Shown on Status tab.</summary>
    public string LastStartError { get; private set; } = string.Empty;
    /// <summary>Set when Start fails validation; the UI consumes this to switch to the Status tab once.</summary>
    public bool ForceStatusTab { get; set; }
    public string StatusText { get; private set; } = "Idle";
    public SessionStats Stats { get; } = new();

    private Configuration C => Plugin.C;

    // ---------------------------------------------------------------- diagnostics
    // Throttled, area-tagged logging for our recurring problem areas (NPC interaction, combat,
    // movement, collect, escort). Gated by the single C.VerboseLogging toggle (Status tab). Each
    // area is throttled by a key so the log isn't flooded.
    private void Diag(string area, string key, string msg)
    {
        if (!C.VerboseLogging) return;
        if (!EzThrottler.Throttle($"AFDiag_{area}_{key}", 500)) return;
        Svc.Log.Info($"[Diag/{area}] {msg}");
    }

    // Last state we logged a transition for (see Tick).
    private FarmState _lastLoggedState = FarmState.Idle;

    // A fate can be picked while it is still PREPARING: it is waiting for a player to talk to its
    // "!" start NPC (see FateSelector.IsPreparing), so once inside the ring we find that NPC and
    // start the fate ourselves. Only if there is no start NPC to be found do we hold the spawn
    // point in case it goes live on its own, and not unboundedly: past this we give up and stop
    // considering that fate for as long as it stays preparing.
    private const long PreparingWaitMs = 120000;
    private long _preparingSinceMs;
    private readonly HashSet<ushort> _preparingGaveUp = new();
    // How many times we fired the start-NPC talk on the current preparing fate. A talk that did not
    // take (dialogue cancelled, interact lost to a server hiccup) is retried after a short hold; a
    // fate that still refuses to start after this many talks is given up on like one with no NPC.
    private int _npcStartAttempts;
    private const int NpcStartMaxAttempts = 3;
    private const long NpcStartRetryMs = 8000;

    // Active target fate + zone bookkeeping.
    private ushort _targetFateId;
    private uint _targetTerritory;
    private int _zoneRotationIndex;
    private readonly Dictionary<uint, int> _zoneFatesDone = new();

    // After a gemstone shopping session, remember the gem count so we don't immediately re-enter
    // the shop (continuous-buy entries always "want more"). We only shop again once we've farmed
    // more gems than we had when the last session ended.
    private int _gemCountAfterLastShop = -1;

    // One-shot latch for the CURRENT shop visit. Set the instant a visit is judged complete
    // (everything bought / drained to threshold / capped). While latched we NEVER re-interact the
    // vendor — we only close the window and leave the state. Cleared on entry to a fresh visit
    // (StartShopping) so the next legitimate trip works. This is the fix for the open->buy->close->
    // re-interact->reopen loop: ShouldShop() can still read true after buying, so we must not let
    // the NPC-interact fall-through fire again within the same visit.
    private bool _shoppingDone;

    // What we were doing when the user hit pause, so the overlay can show it while held.
    private string _pausedDoing = string.Empty;

    // The farming state we were in before diverting to gemstone shopping. When shopping completes
    // we return directly to this (fate grinding / chocobo loop) instead of routing back through the
    // maintenance check, which would otherwise immediately re-enter the shop.
    // After shopping we always return to SelectingZone — we're standing in the VENDOR's zone, so we
    // must re-pick a farming zone for the current mode and travel back, not look for fates here.
    private FarmState _stateBeforeShop = FarmState.SelectingZone;

    // Zone dwell: when we last saw an active FATE in the current zone. We stay and wait for FATEs
    // to respawn rather than zone-hopping the instant a zone is empty.
    private long _lastFateSeenMs;
    private uint _dwellZone;

    // Re-open the Shared FATE window to refresh data every 5 completed fates.
    private int _fatesSinceFateDataRefresh;

    // Collect-fate hand-in: we hand in fixed batches (DWD/BMR-style) rather than trying to "learn"
    // the per-item progress value, which was fragile and never calibrated. Hold until we have a
    // full batch, hand it in, repeat until the fate hits 100% (or runs dry of ground items).
    private const int CollectBatchSize = 10; // collect fates reward "gold" at 10 turned in

    // A collect fate runs to a fixed number of hand-ins (20 on effectively every one of them). We
    // don't need that number to farm the fate, but we do need to know when the batch in our bags is
    // the one that CLOSES it: past that point every mob killed and every item grabbed is thrown
    // away, so the batch goes in immediately instead of after the next fight.
    private const int CollectDefaultGoal = 20;
    // Sanity window for a derived goal: real collect fates ask for somewhere between 8 and 40
    // items. Outside that, HandInCount isn't counting what we think it is and we use the default.
    private const int CollectMinGoal = 8;
    private const int CollectMaxGoal = 40;
    // Last phase: with this little time left, another gather-fight-carry cycle doesn't fit. We
    // deliver what we hold (it still counts) and move to a fate that can still pay out.
    private const long CollectEndgameSeconds = 60;
    // Delivering with a train behind us: a mob this low is finished off (a couple of GCDs) rather
    // than dragged to the NPC, since killing it is what actually removes it. Healthier ones are
    // outrun instead.
    private const float CollectFinishHpFraction = 0.30f;
    private const float CollectFinishRange = 5f;   // it has to be in reach: we stand still to kill it
    private const long CollectFinishTimeoutMs = 6000; // and if it isn't dead by then, it isn't dying
    // Shedding: we run the spawn-center -> NPC line out past the ring, where fate mobs leash off,
    // then walk back to a quiet NPC. Bounded — a hand-in with mobs on us still beats no hand-in.
    private const float CollectShedMargin = 15f;      // yalms past the fate radius
    private const long CollectShedTimeoutMs = 10000;
    private const long CollectShedCooldownMs = 20000; // one shed run per delivery, not a ping-pong
    private const float CollectQuietRange = 12f;      // an attacker this close = not clear to turn in

    // Tracks whether the rotation backend / BMR AI are currently running (so we toggle them only on
    // transitions, not every tick). During collect HandIn/Pickup we STOP the rotation so it doesn't
    // fight us for the target — that target tug-of-war was the "flicks between the NPC and an
    // enemy" bug. The two are tracked separately because travel turns the rotation off while
    // leaving the AI on to dodge.
    private bool _rotationActive;
    private bool _aiActive;
    private bool _bmrStrayFocused; // BMR is pinned to our target for ClearingAggro (see Tick)

    // Aetheryte shortcut: when the fate we picked is far away and this zone has an attuned
    // aetheryte closer to it, teleport there first instead of flying the whole way. Decided ONCE
    // per fate (the latch), and any aetheryte a teleport fails to reach is dropped for the session
    // so we don't burn 5s on it every fate.
    private ushort _hopEvaluatedFateId;
    private ushort _hopAirborneLoggedFateId; // fate we already logged "airborne, no teleport" for
    private uint _hopAetheryteId;
    private System.Numerics.Vector2 _hopDestination; // world X/Z of the aetheryte we're teleporting to
    private long _hopIssuedMs;
    private long _hopLastBusyMs;
    private readonly HashSet<uint> _hopFailedAetherytes = new();
    // How close to the aetheryte counts as "the teleport landed".
    private const float HopArrivalDistance = 40f;
    // Grace after issuing (or after the last busy tick) before we call the teleport a no-show.
    private const long HopSettleMs = 4000;
    // Hard cap on the whole hop, in case we get stuck in a loading/casting state.
    private const long HopTimeoutMs = 45000;

    // Fate-travel stuck detection: if we barely move for >2s while traveling to the fate dropoff,
    // the spot is unreachable -> re-roll a NEW random LANDABLE point in the ring and go to that.
    private System.Numerics.Vector3 _fateStuckLastPos;
    private long _fateStuckLastSampleMs;
    private const long FateStuckWindowMs = 2000;
    private const float FateStuckMinMove = 2f;

    // In-fate UNLANDABLE recovery: rarely we land on a spot that isn't actually landable, so the
    // dismount keeps failing and we sit MOUNTED + stationary inside the ring. If that persists past
    // FateStuckWindowMs, re-roll a NEW random LANDABLE point in the ring and navigate to it. Once
    // we're DISMOUNTED we've made it (no re-roll). Dedicated sampler so it can't clash with the
    // TravelingToFate stuck sampler.
    private System.Numerics.Vector3 _inFateMountPos;
    private long _inFateMountMs;

    // Grounded-stuck escape: while on foot and navigating, if we move < GroundStuckMinMove over
    // GroundStuckWindowMs we're wedged on geometry. Back straight out ~5y, then regenerate the path.
    private System.Numerics.Vector3 _groundStuckLastPos;
    private long _groundStuckLastMs;
    // Jump phase that runs BEFORE backing out: jump, wait for grounded, jump again, then wait the
    // SAME window; only if still not moved do we fall through to back-out + renav.
    private int _groundJumpPhase;                 // 0=idle, 1=did first jump (await grounded), 2=did second jump (await window)
    private System.Numerics.Vector3 _groundJumpStartPos;
    private long _groundJumpWaitUntilMs;
    private System.Numerics.Vector3? _groundNudgeTarget; // back-out point we're driving to
    private long _groundNudgeUntilMs;                    // safety timeout for the nudge
    private bool _groundNudgeFly;                        // back-out nudge should use flight pathing
    private const long GroundStuckWindowMs = 2000;
    private const float GroundStuckMinMove = 2f;
    private void SetCombatBackend(bool active)
    {
        SetRotationActive(active);
        SetAiActive(active);
    }

    /// <summary>
    /// The damage rotation on its own. Travel legs run with this OFF so the rotation doesn't pick
    /// fights with everything we pass, while BMR's AI stays on and keeps dodging.
    /// </summary>
    private void SetRotationActive(bool active)
    {
        if (_rotationActive == active) return;
        _rotationActive = active;
        if (active) IPCManager.StartRotation(C);
        else IPCManager.StopRotation(C);
    }

    /// <summary>BMR's AI (in-combat movement + AOE dodging).</summary>
    private void SetAiActive(bool active)
    {
        if (_aiActive == active) return;
        _aiActive = active;
        IPCManager.SetAi(active);
    }


    // Smart-mix yield latch: when BMR takes over for a dodge we keep yielding until danger has
    // been fully clear for this settle window, so vnav and BMR don't fight for control.
    private long _yieldUntilMs;
    private const long YieldSettleMs = 600;

    // ---------------------------------------------------------------- lifecycle
    public void Start()
    {
        if (Running) return;

        if (!IPCManager.ValidateBackends(C, out var error))
        {
            Svc.Chat.PrintError($"[Autofate] {error}");
            LastStartError = error;
            ForceStatusTab = true; // tell the UI to jump to the Status tab so the user sees the error
            return;
        }
        LastStartError = string.Empty;

        Running = true;
        Paused = false;
        LastStopReason = StopReason.None;
        Stats.Reset();
        _zoneRotationIndex = 0;
        _zoneFatesDone.Clear();
        _dwellZone = 0;
        _lastFateSeenMs = Environment.TickCount64;
        _preparingGaveUp.Clear();
        // Reset the shopping latch so we re-evaluate buying fresh on every Start.
        _gemCountAfterLastShop = -1;

        // ON START: open the Shared FATE window once to (re)populate the tracker cache. Invalidating
        // forces EnsureData to re-open, read, and spam-close it on the next ticks.
        _fatesSinceFateDataRefresh = 0;
        Features.SharedFateTracker.RefreshData(force: true);

        // ON START: if we already have the gemstones (>= threshold) and we still need items (any
        // enabled entry's inventory count is below its target), go buy immediately before farming.
        // Otherwise start the normal zone-selection loop.
        if (GemstoneShopper.ShouldShop(C))
        {
            EnterShopping();
            StatusText = "Starting — buying gemstone items first...";
            Svc.Chat.Print("[Autofate] Farming started — buying gemstone items first.");
        }
        else
        {
            State = FarmState.SelectingZone;
            StatusText = "Starting...";
            Svc.Chat.Print("[Autofate] Farming started.");
        }
        SetCombatBackend(true);
        TextAdvanceIPC.Enable(); // hand ALL our dialogue (Talk/Yes-No/reward/cutscene/turn-in) to TextAdvance
        Svc.Log.Information("[Autofate] Started in mode " + C.Mode);
    }

    public void Stop(StopReason reason = StopReason.UserRequested)
    {
        if (!Running) return;
        Running = false;
        Paused = false;
        LastStopReason = reason;
        State = FarmState.Stopped;
        StatusText = $"Stopped ({reason})";
        Navigator.Stop();
        // Hard-disable EVERY combat/movement IPC (rotation backends, BMR AI + follow/forbid flags,
        // vnavmesh) regardless of the configured backend, so nothing keeps running after Stop.
        IPCManager.ShutdownAll();
        TextAdvanceIPC.Disable();     // release any collect turn-in control
        _rotationActive = false;      // re-sync the combat latches so the next Start re-issues
        _aiActive = false;
        _bmrStrayFocused = false;     // ShutdownAll dropped the stray-aggro pins

        Svc.Chat.Print($"[Autofate] Farming stopped: {reason}.");
        Svc.Log.Information($"[Autofate] Stopped: {reason}");

        // Optional lifestream-to-destination when finishing.
        if (reason != StopReason.Error && C.LifestreamOnFinish && !string.IsNullOrWhiteSpace(C.LifestreamFinishCommand))
        {
            Teleporter.LifestreamCommand(C.LifestreamFinishCommand);
        }
    }

    public void Toggle()
    {
        if (Running) Stop();
        else Start();
    }

    /// <summary>
    /// Hold the run in place: stop navigation, shut every combat/movement backend down, but keep
    /// Running, the current state and all session counters. What we CANNOT do is unwind a dialogue
    /// or cutscene already in flight, and with the AI backend off nothing is dodging for us, so
    /// pausing mid-pull is dangerous.
    /// </summary>
    public void Pause()
    {
        if (!Running || Paused) return;
        Paused = true;
        _pausedDoing = StatusText;
        StatusText = $"Paused - held at: {_pausedDoing}";
        Stats.OnPaused();
        Navigator.Stop();
        IPCManager.ShutdownAll();
        TextAdvanceIPC.Disable();
        _rotationActive = false;      // re-sync the latches so Resume re-issues
        _aiActive = false;
        _bmrStrayFocused = false;     // ShutdownAll dropped the stray-aggro pins
        Svc.Chat.Print("[Autofate] Paused.");
        Svc.Log.Information($"[Autofate] Paused in state {State}");
    }

    /// <summary>Pick the run back up where it left off.</summary>
    public void Resume()
    {
        if (!Running || !Paused) return;
        Paused = false;
        Stats.OnResumed();

        // A fate we were holding has almost certainly expired over a long pause. Drop it and
        // re-select rather than pathing to a fate that is no longer there.
        if (State is (FarmState.InFate or FarmState.CollectTurnIn or FarmState.TravelingToFate)
            && (_targetFateId == 0 || FateSelector.GetFateById(_targetFateId) == null))
        {
            _targetFateId = 0;
            State = FarmState.SelectingFate;
        }

        StatusText = "Resuming...";
        SetCombatBackend(true);
        TextAdvanceIPC.Enable();
        Svc.Chat.Print("[Autofate] Resumed.");
        Svc.Log.Information($"[Autofate] Resumed into state {State}");
    }

    public void TogglePause()
    {
        if (Paused) Resume();
        else Pause();
    }

    // ---------------------------------------------------------------- main tick
    public void Tick()
    {
        if (!Running) return;
        if (Paused) return;
        if (Player.Object == null) return;             // not logged in
        if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]) return;

        // One line per state change, so a log tells you where the time went without guessing from
        // the combat/movement chatter. Logged at the top of the tick AFTER the transition, so
        // StatusText already describes the state we moved into.
        if (_lastLoggedState != State)
        {
            if (C.VerboseLogging) Svc.Log.Information($"[Diag/State] {_lastLoggedState} -> {State} | {StatusText}");
            _lastLoggedState = State;
        }

        Stats.CurrentLevel = Player.Level;
        Stats.SampleGemstones();
        Stats.SampleDeaths();

        // Hard stop triggers (checked every tick).
        if (CheckStopTriggers()) return;

        // DEAD: park everything until we're back up. Nothing below this line makes sense on a
        // corpse — it used to keep targeting mobs, then "walk" to the next fate (0y moved, jump,
        // back out, repeat) until someone noticed.
        if (TickDead()) return;

        // Keep BMR's AutoTarget fate scoping alive. These are TRANSIENT strategies, so BMR drops
        // them on its own (zone change, preset re-activation) — pushing them only when the rotation
        // is switched on meant the scoping could lapse for the rest of the run without a trace.
        // Self-throttled to one push every 2s.
        //
        // Held back while we're killing a stray: the hint tells BMR's AutoTarget to prefer fate
        // mobs, which is the wrong instruction when the thing we need dead is not one. This only
        // stops us re-asserting it — a scoping BMR already holds stays until BMR drops it — so if
        // the rotation still pulls back to fate mobs mid-stray, that is where to look.
        //
        // Clearing stray aggro between fates goes further and pins BMR to our target: the preset's
        // Aggressive AutoTarget and AOE rotations otherwise hit the passive mobs standing next to
        // the one that stopped us, which aggroes them and turns a quick kill into a brawl. The pin
        // is re-pushed on a throttle (transients lapse on BMR's side) and dropped the moment we are
        // in any other state, whichever way we left ClearingAggro.
        if (State == FarmState.ClearingAggro)
        {
            IPCManager.ApplyBmrStrayFocus();
            _bmrStrayFocused = true;
        }
        else
        {
            if (_bmrStrayFocused)
            {
                IPCManager.ClearBmrStrayFocus();
                _bmrStrayFocused = false;
            }
            if (_rotationActive && _strayTargetId == 0) IPCManager.ApplyBmrFateTargeting(C);
        }

        // Always-on maintenance that can run in parallel with farming.
        ConsumableManager.Tick(C);
        // Skip companion maintenance while stabling: the stable routine WITHDRAWS the chocobo, and
        // auto-summon would immediately try to re-summon it (-> "unable to summon companion here"
        // in housing) and fight our own Withdraw.
        if (State != FarmState.ChocoboLeveling)
            ChocoboManager.Tick(C);

        // In Shared FATEs mode, keep the in-game shared-fate tracker data loaded so zone
        // skip/stop logic has something to read. EnsureData opens the window once (driven by the
        // RefreshData(force) invalidation in Start), caches the data, then spam-closes it.
        if (C.Mode == FarmingMode.SharedFates)
        {
            // EnsureData opens the window once, caches the data, then spam-closes it.
            Features.SharedFateTracker.EnsureData();
            // If the window is open (ours or the user's), re-read live so the zone we're actively
            // progressing updates instead of showing a stale cached value.
            Features.SharedFateTracker.CaptureIfWindowOpen();
        }

        // SYNC ASAP: if we're physically standing inside a running fate and not yet level-synced,
        // sync immediately — handles the case where the plugin is started while already in a fate.
        if (C.AutoLevelSync && IsInsideAnyRunningFate())
            SyncToFate();

        // Stray-aggro guard: if we're between fates (selecting/traveling) and something hostile is
        // beating on us or our chocobo, drop into ClearingAggro to deal with it first. Checked
        // continuously (not just at fate-end) since aggro can land at any time.
        //
        // NEVER while airborne. Nothing on the ground can reach us up there and the mob that tagged
        // us leashes on its own, so breaking off only means landing somewhere random and throwing
        // the whole flight away — which is exactly the "flying to a fate, got aggroed, stopped and
        // unmounted" case. We keep flying and let it fall off.
        //
        // The trigger is a REAL attacker, not the in-combat flag. The flag lingers after a mob has
        // given up, so on the tick we touch down at the fate it used to drag us straight back out
        // of the fate we had just arrived at. This also matches what TickClearingAggro actually
        // does — it only ever fights things that target us or our chocobo.
        if ((State == FarmState.SelectingFate || State == FarmState.TravelingToFate
             || State == FarmState.SelectingZone || State == FarmState.TravelingToZone)
            && !MountManager.IsFlying
            && FateTargeting.GetEnemiesAttackingMe().Count > 0)
        {
            Navigator.Stop();
            State = FarmState.ClearingAggro;
        }
        else if (MountManager.IsFlying && InCombat()
                 && (State == FarmState.TravelingToFate || State == FarmState.TravelingToZone))
        {
            Diag("Combat", "flyaggro", "aggroed mid-flight -> ignoring it and staying on the mount");
        }

        // Grounded-stuck escape (back out + regenerate) — consumes the tick if active.
        if (TickGroundedStuck()) return;

        // FOLLOW-LEADER: skip the whole zone-select/travel state machine. We don't pick or path to
        // our own fates — just follow the leader wherever they go and run whatever fate we land in.
        // (Still allow combat/collect handling once we're physically inside a fate.)
        if (C.FollowPartyLeader
            && State is not (FarmState.InFate or FarmState.CollectTurnIn or FarmState.ClearingAggro
                             or FarmState.TravelingToFate
                             or FarmState.Maintenance or FarmState.ChocoboLeveling or FarmState.GemstoneShopping))
        {
            TickFollowLeader();
            return;
        }

        // QUIET TRAVEL: no rotation while we're picking a fate or on the way to one. The rotation
        // (and BMR's AutoTarget with it) otherwise engages whatever we happen to pass, which is
        // both a detour and a good way to arrive at the fate in combat with something else. BMR's
        // AI stays on so AOEs are still dodged, and the stray-aggro guard above hands us to
        // ClearingAggro — which turns the rotation back on — the moment something actually hits us.
        if (State is FarmState.SelectingZone or FarmState.TravelingToZone
                  or FarmState.SelectingFate or FarmState.TravelingToFate)
            SetRotationActive(false);

        switch (State)
        {
            case FarmState.SelectingZone: TickSelectingZone(); break;
            case FarmState.TravelingToZone: TickTravelingToZone(); break;
            case FarmState.SelectingFate: TickSelectingFate(); break;
            case FarmState.TravelingToFate: TickTravelingToFate(); break;
            case FarmState.InFate: TickInFate(); break;
            case FarmState.ClearingAggro: TickClearingAggro(); break;
            case FarmState.CollectTurnIn: TickInFate(); break; // collect handled inside InFate
            case FarmState.Maintenance: TickMaintenance(); break;
            case FarmState.ChocoboLeveling: TickChocoboLeveling(); break;
            case FarmState.GemstoneShopping: TickGemstoneShopping(); break;
            default: State = FarmState.SelectingZone; break;
        }
    }

    // ---------------------------------------------------------------- stop triggers
    // Death handling: when we started being dead (0 = alive) and when we last tried Return.
    private long _deadSinceMs;
    private long _returnLastTryMs;
    private const uint ReturnGeneralAction = 8; // "Return" — revive at the nearest aetheryte

    /// <summary>
    /// Returns true while we're dead, which parks the whole tick. Stops navigation and combat on
    /// the way down, optionally uses Return to get us back up, and resumes farming once we're alive.
    /// </summary>
    private bool TickDead()
    {
        var me = Player.Object;
        var dead = me != null && me.IsDead;

        if (!dead)
        {
            if (_deadSinceMs != 0)
            {
                Svc.Log.Information($"[Autofate] Alive again after {(Environment.TickCount64 - _deadSinceMs) / 1000}s — resuming.");
                _deadSinceMs = 0;
                _returnLastTryMs = 0;
                // We may be anywhere now (raised on the spot, or returned to an aetheryte), so
                // re-pick from the top rather than resuming a fate we're no longer near.
                _targetFateId = 0;
                ResetPerFateState();
                SetAiActive(true);   // hand AOE dodging back; the rotation comes back at the fate
                State = FarmState.SelectingZone;
            }
            return false;
        }

        if (_deadSinceMs == 0)
        {
            _deadSinceMs = Environment.TickCount64;
            Navigator.Stop();
            SetCombatBackend(false);
            State = FarmState.Dead;
            Svc.Log.Warning($"[Autofate] Died (death {Stats.Deaths} this session) — everything parked until we're back up.");
            Svc.Chat.PrintError("[Autofate] You died — paused until you're up again.");
        }

        var downMs = Environment.TickCount64 - _deadSinceMs;
        StatusText = $"Dead — waiting ({downMs / 1000}s)";

        // A raise from another player pops a Yes/No ("accept the offer of resurrection?"), and so
        // does Return's own confirmation — the only two dialogs we can see while down, so one Yes
        // handles both. With raise-accept off we still confirm briefly after firing Return, or the
        // prompt would sit there unanswered.
        var confirmDialog = C.AcceptRaiseAutomatically
            || (_returnLastTryMs != 0 && Environment.TickCount64 - _returnLastTryMs < 10000);
        if (confirmDialog
            && ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectYesno", out var raiseYn)
            && ECommons.GenericHelpers.IsAddonReady(raiseYn))
        {
            if (EzThrottler.Throttle("AF_DeathConfirm", 500))
            {
                try { new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectYesno((nint)raiseYn).Yes(); }
                catch (Exception e) { Svc.Log.Verbose($"[Autofate] Death dialog Yes failed (mid-transition): {e.Message}"); }
            }
            StatusText = "Dead — accepting revival";
            return true;
        }

        // Return revives us at the nearest aetheryte. Delayed so a passing player still has a
        // window to raise us, and retried because it's refused while a raise is pending.
        if (C.AutoReturnOnDeath
            && downMs >= C.DeathReturnDelaySeconds * 1000L
            && Environment.TickCount64 - _returnLastTryMs >= 10000)
        {
            _returnLastTryMs = Environment.TickCount64;
            StatusText = "Dead — returning to the aetheryte";
            try
            {
                FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance()
                    ->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, ReturnGeneralAction);
                Svc.Log.Information("[Autofate] Using Return to revive at the nearest aetheryte.");
            }
            catch (Exception e) { Svc.Log.Warning($"[Autofate] Return failed: {e.Message}"); }
        }

        return true;
    }

    private bool CheckStopTriggers()
    {
        if (C.StopAtLevel && Player.Level >= C.DesiredLevel)
        {
            Stop(StopReason.LevelReached);
            return true;
        }

        // Leveling mode only: if we die more than twice, something is wrong (overtuned mobs, bad
        // pulls) — stop everything (nav + combat) so we don't keep feeding deaths.
        if (C.Mode == FarmingMode.Leveling && Stats.Deaths > 2)
        {
            Svc.Chat.PrintError("[Autofate] Died more than twice in leveling mode — stopping.");
            Stop(StopReason.TooManyDeaths);
            return true;
        }

        if (C.StopAtGemstoneCount && Stats.GemstonesGained >= C.GemstoneStopCount)
        {
            Stop(StopReason.GemstoneCountReached);
            return true;
        }

        // Chocobo maxed -> stop. CRITICAL: do NOT fire this while we're still stabling. Feeding the
        // final onion flips Rank to 20 mid-fetch; if we stopped here we'd halt the plugin before the
        // fetch completes and leave the chocobo stuck in the stable. Only stop once we've left the
        // stable flow (the routine doesn't finish until the chocobo is fetched back out).
        if (C.StopAtChocoboMaxed && C.ChocoboCompanionEnabled && ChocoboManager.ReachedTargetLevel(C)
            && State != FarmState.ChocoboLeveling)
        {
            Stop(StopReason.ChocoboMaxed);
            return true;
        }

        if (C.StopAtVendorRequirementMet && C.EnableGemstoneShopping && C.GemstoneBuyList.Count > 0
            && GemstoneShopper.AllTargetsMet(C)
            && C.GemstoneBuyList.All(e => !e.Enabled || e.TargetQuantity > 0))
        {
            Stop(StopReason.VendorRequirementMet);
            return true;
        }

        // Collection modes (Atma/Demiatma/Memories/Luminous): stop once the WHOLE required list is
        // in inventory. We track item counts directly (one atma each, 3 demiatma each, 20 memories
        // each, one luminous crystal each).
        if (Data.CollectionRequirements.IsCollectionMode(C.Mode)
            && Data.CollectionRequirements.AllSatisfied(C.Mode))
        {
            Stop(StopReason.CollectionComplete);
            return true;
        }

        // All shared-fate zones maxed (only when we have the tracker data to back it up).
        if (C.Mode == FarmingMode.SharedFates && C.StopWhenAllSharedFatesMaxed
            && Features.SharedFateTracker.HasData())
        {
            var sharedZones = Data.Zones.SharedFateZones(C.SelectedSharedFateExpansions()).Select(z => z.TerritoryId).ToHashSet();
            var tracked = Features.SharedFateTracker.GetAllZones()
                .Where(z => sharedZones.Contains(z.TerritoryId))
                .ToList();
            if (tracked.Count > 0 && tracked.All(z => z.IsMaxed))
            {
                Stop(StopReason.AllSharedFatesMaxed);
                return true;
            }
        }

        // Repair safety: out of dark matter for self-repair.
        if (C.AutoRepair && C.RepairMode == RepairMode.SelfRepair
            && RepairManager.NeedsRepair(C) && !RepairManager.CanSelfRepair())
        {
            Svc.Chat.PrintError("[Autofate] Out of dark matter for self-repair. Stopping.");
            Stop(StopReason.OutOfDarkMatter);
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- zone selection
    private uint[] GetModeZones()
    {
        switch (C.Mode)
        {
            case FarmingMode.SingleZone:
                return C.SingleZoneTerritory != 0 ? new[] { C.SingleZoneTerritory } : Array.Empty<uint>();
            case FarmingMode.Manual:
                return C.ManualZones.Select(z => z.TerritoryId).Where(t => t != 0).ToArray();
            case FarmingMode.Atma:
            case FarmingMode.Demiatma:
            case FarmingMode.LuminousCrystals:
            case FarmingMode.Memories:
            {
                // Only include zones that still host an UNMET collectable. Once every item a zone
                // drops is in inventory, drop it from the rotation so we move on and never return.
                var unmet = Data.CollectionRequirements.UnsatisfiedZones(C.Mode);
                return Data.Zones.ForMode(C.Mode)
                    .Where(z => unmet.Contains(z.PlaceName))
                    .Select(z => z.TerritoryId).Where(t => t != 0).ToArray();
            }
            case FarmingMode.SharedFates:
            {
                var all = Data.Zones.SharedFateZones(C.SelectedSharedFateExpansions()).Select(z => z.TerritoryId);
                // Drive logic from the in-game Shared FATE tracker: skip zones whose shared-fate
                // rank is already maxed (only filter when we actually have the agent data).
                if (C.SharedFateSkipMaxed && Features.SharedFateTracker.HasData())
                    all = all.Where(t => !Features.SharedFateTracker.IsZoneMaxed(t));
                // Only zones with FATEs we may run, best for our level first. The round-robin
                // starts at the front, so this is also where we go from a city.
                return Logic.SharedFateZones.Order(all, Player.Level, C.LevelsAbovePlayer);
            }
            case FarmingMode.Leveling:
            {
                // Pick the best FATE-grinding zone for the player's level, CAPPED at LevelingZoneCap.
                // So if the cap is 50 we keep farming the level-50 zone even after hitting 50; the
                // actual stop is handled by the Stop Triggers tab (DesiredLevel). As the player
                // levels up (below the cap) the picker advances zones automatically.
                var effLevel = Math.Min(Player.Level, C.LevelingZoneCap);
                var t = Data.LevelingZones.BestTerritoryForLevel(effLevel);
                return t != 0 ? new[] { t } : new[] { Svc.ClientState.TerritoryType };
            }
            default:
                return Array.Empty<uint>();
        }
    }

    private void TickSelectingZone()
    {
        StatusText = "Selecting zone";

        // Maintenance gating before we pick a zone (repair/chocobo/shop take priority).
        if (TryEnterMaintenance()) return;

        var here = Svc.ClientState.TerritoryType;

        // If the current zone is a valid farming zone and has candidate fates, stay.
        var zones = GetModeZones();
        if (zones.Length == 0)
        {
            if (EzThrottler.Throttle("AF_NoZones", 10000))
                Svc.Chat.PrintError(C.Mode == FarmingMode.SharedFates
                    ? "[Autofate] No unfinished Shared FATE zone has FATEs at your level."
                    : "[Autofate] No zones configured for this mode.");
            return;
        }

        // Manual mode rotation handling.
        if (C.Mode == FarmingMode.Manual)
        {
            HandleManualZoneSelection(zones);
            return;
        }

        // SHARED FATES: if the zone we're standing in is already COMPLETE (60 fates), don't farm it —
        // leave and move to the next incomplete zone. GetModeZones() already filters maxed zones, so
        // a complete `here` won't be in `zones`; falling through to the round-robin travel handles
        // the move. (This is the fix for "Lakeland complete but it won't leave".)
        if (C.Mode == FarmingMode.SharedFates && C.SharedFateSkipMaxed
            && Features.SharedFateTracker.HasData()
            && Features.SharedFateTracker.IsZoneMaxed(here))
        {
            if (EzThrottler.Throttle("AF_ZoneComplete", 5000))
                Svc.Log.Information($"[Zone] {Data.Zones.GetTerritoryName(here)} shared FATEs complete (60) — moving on.");
            _targetTerritory = zones[_zoneRotationIndex % zones.Length];
            State = FarmState.TravelingToZone;
            return;
        }

        // If we're already in one of the mode's zones, hand off to fate selection. The dwell
        // logic there waits for FATEs to (re)spawn and only rotates zones after ZoneDwellSeconds
        // of being dry — we must NOT teleport away just because no FATE is active this instant.
        if (zones.Contains(here))
        {
            _targetTerritory = here;
            State = FarmState.SelectingFate;
            return;
        }

        // We're not in a mode zone — travel to the next zone in the round-robin.
        _targetTerritory = zones[_zoneRotationIndex % zones.Length];
        State = FarmState.TravelingToZone;
    }

    private void HandleManualZoneSelection(uint[] zones)
    {
        var entries = C.ManualZones.Where(z => z.TerritoryId != 0).ToList();
        if (entries.Count == 0) return;

        // Find the current entry; advance if its quota is met.
        if (_zoneRotationIndex >= entries.Count)
        {
            if (C.ManualLoop) _zoneRotationIndex = 0;
            else { Stop(StopReason.UserRequested); return; }
        }
        var entry = entries[_zoneRotationIndex];

        if (entry.FatesToRun > 0 && entry.FatesDone >= entry.FatesToRun)
        {
            _zoneRotationIndex++;
            return;
        }

        _targetTerritory = entry.TerritoryId;
        if (Svc.ClientState.TerritoryType == _targetTerritory)
            State = FarmState.SelectingFate;
        else
            State = FarmState.TravelingToZone;
    }

    private void TickTravelingToZone()
    {
        StatusText = $"Traveling to {Data.Zones.GetTerritoryName(_targetTerritory)}";
        if (Teleporter.TravelToTerritory(C, _targetTerritory))
        {
            // Fresh dwell window for the newly-entered zone.
            _dwellZone = 0;
            _lastFateSeenMs = Environment.TickCount64;
            State = FarmState.SelectingFate;
        }
    }

    // ---------------------------------------------------------------- fate selection
    private void TickSelectingFate()
    {
        // Follow-party-leader mode: don't pick our own fate, just follow.
        if (C.FollowPartyLeader)
        {
            TickFollowLeader();
            return;
        }

        if (TryEnterMaintenance()) return;

        var here = Svc.ClientState.TerritoryType;
        var now = Environment.TickCount64;

        // SHARED FATES: if this zone just hit 60 (complete) while we were farming it, stop and go
        // pick the next incomplete zone immediately — don't keep running fates here.
        if (C.Mode == FarmingMode.SharedFates && C.SharedFateSkipMaxed
            && Features.SharedFateTracker.HasData()
            && Features.SharedFateTracker.IsZoneMaxed(here))
        {
            Svc.Log.Information($"[Zone] {Data.Zones.GetTerritoryName(here)} shared FATEs complete (60) — leaving.");
            Navigator.Stop();
            State = FarmState.SelectingZone;
            return;
        }

        // COLLECTION MODES: if every collectable this zone drops is already in inventory at the
        // required count, leave and move to the next zone that still has something we need.
        if (Data.CollectionRequirements.IsCollectionMode(C.Mode)
            && Data.CollectionRequirements.ZoneSatisfied(C.Mode, Data.Zones.GetTerritoryName(here)))
        {
            Svc.Log.Information($"[Zone] {Data.Zones.GetTerritoryName(here)} collectables complete — moving on.");
            Navigator.Stop();
            State = FarmState.SelectingZone;
            return;
        }

        // Reset the dwell timer whenever we (re)enter a zone, so we give each zone a full dwell
        // window to spawn a FATE before considering rotating away.
        if (_dwellZone != here)
        {
            _dwellZone = here;
            _lastFateSeenMs = now;
        }

        StatusText = "Selecting fate";
        var best = FateSelector.PickBest(C, _preparingGaveUp, UnreachableFates());
        if (best == null)
        {
            // No valid fate right now. FATEs respawn every few minutes, so DWELL in this zone and
            // wait rather than instantly teleporting away. Only rotate after the zone has been dry
            // for ZoneDwellSeconds (0 = never rotate, stay forever).
            var zones = GetModeZones();
            var dwellMs = C.ZoneDwellSeconds * 1000L;
            var dryFor = now - _lastFateSeenMs;

            if (zones.Length > 1 && C.ZoneDwellSeconds > 0 && dryFor >= dwellMs)
            {
                Svc.Log.Debug($"[Zone] No FATEs for {dryFor / 1000}s in {Data.Zones.GetTerritoryName(here)}; rotating to next zone.");
                _zoneRotationIndex++;
                _lastFateSeenMs = now;   // reset so the next zone gets a fresh dwell window
                _dwellZone = 0;
                State = FarmState.SelectingZone;
            }
            else
            {
                var remain = Math.Max(0, (dwellMs - dryFor) / 1000);
                // Say so when there ARE fates here, just none we may run: "waiting for FATEs" reads
                // as a spawn problem when the real problem is our level.
                var tooHigh = Svc.Fates.Count(f => f != null && f.State == FateState.Running
                                                   && f.Level > Player.Level + C.LevelsAbovePlayer);
                var what = tooHigh > 0 ? $"No FATEs at your level here ({tooHigh} above it)" : "Waiting for FATEs";
                StatusText = zones.Length > 1 && C.ZoneDwellSeconds > 0
                    ? $"{what} ({remain}s before rotating zones)"
                    : tooHigh > 0 ? what : "Waiting for FATEs to spawn";
            }
            return;
        }

        // We have a live FATE here — refresh the dwell timer.
        _lastFateSeenMs = now;

        // POST-COMPLETION GRACE: if we just finished a fate, hold briefly before committing to a
        // FAR fate so a chained replacement spawning at/near our spot can be picked instead. A
        // nearby fate (likely the replacement) is taken immediately.
        if (now < _postFateGraceUntilMs && best.Value.Distance > PostFateNearbyDist)
        {
            StatusText = "Waiting for a possible replacement FATE...";
            return;
        }
        _postFateGraceUntilMs = 0; // committing now -> clear the grace window

        if (C.VerboseLogging)
        {
            // Why this fate and not the closer one: dump every candidate with its distance and
            // timer next to the pick. With PrioritizeLowTimer on, the nearest fate is NOT expected
            // to win unless it's within the proximity override.
            var alts = string.Join(", ", FateSelector.GetCandidates(C, _preparingGaveUp, UnreachableFates())
                .OrderBy(x => x.Distance)
                .Select(x => $"'{x.Fate.Name}' {x.Distance:F0}y/{Timer(x)}"));
            Svc.Log.Information($"[Diag/Fate] picked '{best.Value.Fate.Name}' {best.Value.Distance:F0}y/"
                + $"{Timer(best.Value)} type={best.Value.Type} prioritizeLowTimer={C.PrioritizeLowTimer} | {alts}");
        }

        _targetFateId = best.Value.Fate.FateId;
        _startedFateId = 0; // new fate -> allow the start-NPC talk again
        _fateNpcInteractedMs = 0; // clear the post-interact hold for the new fate
        ResetPerFateState(); // clear escort/collect carry-over from any previous fate
        State = FarmState.TravelingToFate;
    }

    /// <summary>A candidate's timer for the diagnostic line: a preparing fate has none to print.</summary>
    private static string Timer(FateSelector.Candidate c) => c.Preparing ? "not started" : $"{c.TimeRemaining}s";

    private void TickTravelingToFate()
    {
        var fate = FateSelector.GetFateById(_targetFateId);
        if (fate == null || fate.State == FateState.Ended || fate.State == FateState.Failed)
        {
            Navigator.Stop();
            State = FarmState.SelectingFate;
            return;
        }

        var me0 = Player.Object;
        if (me0 == null) return;

        // Optional shortcut: teleport to an aetheryte closer to the fate before we start flying.
        // Owns the tick while the teleport is in flight, so don't move us during it.
        if (TickAetheryteHop(fate)) return;

        var type = FateSelector.Classify(fate);
        var insideRing = Vector3.Distance(me0.Position, fate.Position) <= fate.Radius;

        // NPC-start fates (Escort/Defend, a not-yet-started Collect with no enemies, or any fate
        // still PREPARING, i.e. waiting for someone to talk to its "!" NPC): travel to the START
        // NPC. Arrival for these is "inside the ring" — TickInFate then walks the rest of the way to
        // the NPC and drives the talk.
        var startNpcNeeded = type is FateType.Escort or FateType.Defend
            || FateSelector.IsPreparing(fate)
            || (type == FateType.Collect && FateTargeting.GetNearestFateEnemy(_targetFateId) == null);
        // If we couldn't dismount where the ring first took us, _landOnDropoff sends us down the
        // dropoff path below instead: a landable spot, then the NPC on foot from there.
        if (startNpcNeeded && !_landOnDropoff)
        {
            if (insideRing) { ArriveAtFate(fate); return; }
            var npc = FateTargeting.FindFateStartNpc(_targetFateId, fate.Radius);
            var dest = npc?.Position ?? fate.Position;
            // No other spot to try here: if we can't get any closer to the NPC, give up on the fate.
            if (_travelProgress.Update(Environment.TickCount64, Vector3.Distance(me0.Position, dest), WaitingOnNav()))
            {
                GiveUpUnreachable(fate, "can't get any closer to its start NPC");
                return;
            }
            StatusText = $"Traveling to fate NPC: {fate.Name}";
            Navigator.MoveTo(C, dest, 3.5f, allowMount: true);
            return;
        }

        // Combat fates (and Collect fates already underway): travel to a RANDOM LANDABLE interior
        // point. ARRIVAL = on that dropoff: horizontally within a few yalms and at its height or a
        // little above (see FateLanding). The dropoff is inside the ring by construction, so that
        // also means we're in the fate. That decisively ends fate-travel nav, drops us, and hands
        // off to enemy navigation (TickInFate). We do NOT arrive on inside-ring alone (that dropped
        // us at the edge / re-navved).
        _fateDropoff ??= RandomPointInFate(fate);
        // SAFE: don't land in a pack. From afar the fate's mobs aren't loaded yet, so once we're
        // close enough to see them, re-pick the dropoff among several landable spots, taking the
        // one clear of hostiles (Logic.SafeLanding). Once per fate.
        if (!_dropoffSafetyChecked && FateTargeting.EffectivePullStyle(C) == Logic.PullStyle.Safe
            && Vector3.Distance(me0.Position, fate.Position) <= fate.Radius + SafeLandingCheckRange)
        {
            _dropoffSafetyChecked = true;
            var spots = Enumerable.Range(0, 10).Select(_ => RandomPointInFate(fate)).Append(_fateDropoff.Value).ToList();
            var hostiles = FateTargeting.GetHostilesAround(fate.Position, fate.Radius + Logic.SafeLanding.Clearance);
            var pick = Logic.SafeLanding.Pick(me0.Position, spots, hostiles);
            if (pick != _fateDropoff.Value)
            {
                Diag("Movement", "safelanding", $"{hostiles.Count} hostiles around '{fate.Name}' -> landing at {pick} instead of {_fateDropoff}");
                _fateDropoff = pick;
                _climbingOut = false;
                _travelProgress.Reset();
                Navigator.Stop();
            }
        }
        var landing = Logic.FateLanding.Classify(me0.Position, _fateDropoff.Value);
        if (landing == Logic.FateLanding.State.Arrived)
        {
            _climbingOut = false;
            ArriveAtFate(fate);
            return;
        }
        // UNDER THE FLOOR: at the dropoff horizontally but below it, i.e. under a floating island
        // (Ultima Thule, Elpis). Flying straight up goes nowhere, so aim at open air well above the
        // dropoff until we're over the floor again; vnav routes around the island's edge to get there.
        if (landing == Logic.FateLanding.State.UnderFloor && !_climbingOut)
        {
            _climbingOut = true;
            Navigator.Stop();
            Diag("Movement", "underfloor", $"under the floor at the dropoff (me={me0.Position} dropoff={_fateDropoff}) -> climbing out");
        }
        else if (_climbingOut && Logic.FateLanding.ClimbedOut(me0.Position, _fateDropoff.Value))
        {
            _climbingOut = false;
            Diag("Movement", "underfloor", "above the floor again -> landing");
        }

        // STUCK -> re-roll: if we barely move for >2s the dropoff is unreachable; pick a new random
        // landable interior point and head there instead. ONLY while genuinely EN ROUTE — never once
        // we're near the dropoff. During the final descent/landing our HORIZONTAL movement is tiny
        // (we're dropping vertically + decelerating), which read as "stuck" and re-rolled the point
        // right before we landed. Suppress the check when we're already close to the dropoff.
        var nowMs = Environment.TickCount64;
        var nearDropoff = Vector3.Distance(me0.Position, _fateDropoff.Value) <= 8f;
        // WAITING ON vnavmesh IS NOT BEING STUCK. A long flying pathfind can take tens of seconds,
        // and standing still during it is exactly what it looks like. Re-rolling the dropoff then
        // made it worse: every re-roll changed the destination, so the path we finally got back was
        // for a point we no longer wanted, and we'd start the whole wait again. Same for mounting
        // and any other occupied state.
        var waitingOnNav = NavmeshIPC.PathfindInProgress() || ECommons.GenericHelpers.IsOccupied();
        if (nearDropoff || waitingOnNav)
        {
            _fateStuckLastSampleMs = 0; // reset the sampler; we're landing or waiting, not stuck
        }
        else if (_fateStuckLastSampleMs == 0) { _fateStuckLastSampleMs = nowMs; _fateStuckLastPos = me0.Position; }
        else if (nowMs - _fateStuckLastSampleMs >= FateStuckWindowMs)
        {
            if (Vector3.Distance(me0.Position, _fateStuckLastPos) < FateStuckMinMove)
            {
                Navigator.Stop();
                _fateDropoff = RandomPointInFate(fate);
                _climbingOut = false;
                Diag("Movement", "fatestuck", $"stuck >{FateStuckWindowMs}ms -> new random dropoff {_fateDropoff}");
            }
            _fateStuckLastSampleMs = nowMs;
            _fateStuckLastPos = me0.Position;
        }

        StatusText = $"Traveling to fate: {fate.Name}";
        if (EzThrottler.Throttle("AF_TravelProgress", 5000))
            Diag("Travel", "progress", $"'{fate.Name}' fateDist={Vector3.Distance(me0.Position, fate.Position):F0}y "
                + $"dropoffDist={Vector3.Distance(me0.Position, _fateDropoff.Value):F0}y "
                + $"navRunning={NavmeshIPC.IsRunning()} pathfinding={NavmeshIPC.PathfindInProgress()} "
                + $"mounted={Features.MountManager.IsMounted} flying={Features.MountManager.IsFlying} "
                + $"meshReady={NavmeshIPC.MeshReady()} landing={landing} climbingOut={_climbingOut}");
        // Flying: aim above the floor, never at it. vnav stops a fly-to once it's within the stop
        // range in 3D, and aimed at the surface it can get there from underneath, through the floor.
        // On foot (or before we've mounted) the dropoff itself is the target.
        var flyingLeg = Features.MountManager.IsMounted && Features.MountManager.ShouldFly(C);
        var flyTo = flyingLeg ? Logic.FateLanding.FlightTarget(_fateDropoff.Value, _climbingOut) : _fateDropoff.Value;

        // NOT GETTING ANY CLOSER. Moving but never closing in means vnav can't route to this spot
        // (it reports "volume search stopped short of the goal" and every retry fails the same way).
        // Try another spot in the fate; after a few, the fate itself is out of reach for now.
        if (_travelProgress.Update(nowMs, Vector3.Distance(me0.Position, flyTo), WaitingOnNav()))
        {
            if (++_dropoffRerolls >= MaxDropoffRerolls)
            {
                GiveUpUnreachable(fate, $"no progress towards {_dropoffRerolls} different spots in it");
                return;
            }
            Navigator.Stop();
            _fateDropoff = RandomPointInFate(fate);
            _climbingOut = false;
            _travelProgress.Reset();
            Diag("Movement", "noprogress", $"not getting closer to the dropoff -> trying another spot {_fateDropoff} ({_dropoffRerolls}/{MaxDropoffRerolls})");
            return;
        }
        Navigator.MoveTo(C, flyTo, Logic.FateLanding.ArriveRadius, allowMount: true);
    }

    // Travel watchdog: see Logic.TravelProgress. 20s of our own time (pathfind waits excluded)
    // without closing in by 10y is a trip that isn't going to arrive.
    private readonly Logic.TravelProgress _travelProgress = new(stallMs: 20000, minGain: 10f);
    private int _dropoffRerolls;
    private const int MaxDropoffRerolls = 3;
    // Fates we couldn't reach, and until when we leave them alone. Bounded rather than forever:
    // a later spawn of the same fate may well start somewhere we can get to.
    private readonly Dictionary<ushort, long> _unreachableUntil = new();
    private const long UnreachableSkipMs = 10 * 60 * 1000;

    private static bool WaitingOnNav() => NavmeshIPC.PathfindInProgress() || ECommons.GenericHelpers.IsOccupied();

    /// <summary>Fates currently on the unreachable list (expired entries dropped).</summary>
    private IReadOnlySet<ushort> UnreachableFates()
    {
        var now = Environment.TickCount64;
        foreach (var id in _unreachableUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            _unreachableUntil.Remove(id);
        return _unreachableUntil.Keys.ToHashSet();
    }

    private void GiveUpUnreachable(IFate fate, string why)
    {
        _unreachableUntil[_targetFateId] = Environment.TickCount64 + UnreachableSkipMs;
        Navigator.Stop();
        AbandonFate($"'{fate.Name}' looks unreachable ({why}); skipping it for {UnreachableSkipMs / 60000} min");
    }

    /// <summary>
    /// We're at our dropoff (inside the ring / reached the random point). Dismount, then sync +
    /// engage. If we've been here &gt;5s and are STILL mounted, vnav has likely wedged on an
    /// unreachable spot, so pick a NEW random interior point and renav to break the stall.
    /// </summary>
    private void ArriveAtFate(IFate fate)
    {
        // STOP navigating the instant we're considered arrived — never re-path inside the ring (that
        // re-nav was the land<->navigate loop). Then just dismount; once grounded, start the fate.
        Navigator.Stop();

        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            // CAN'T LAND HERE. The game only lets us dismount over landable ground; over a pit, water
            // or a steep slope the dismount just doesn't happen, and we'd hover saying "dismounting"
            // forever. NPC-start fates are most exposed, since they "arrive" the moment we cross into
            // the ring, wherever that is. If we're still up after a few seconds, pick a spot vnav
            // says is landable and fly there first.
            var now = Environment.TickCount64;
            if (_dismountSinceMs == 0) _dismountSinceMs = now;
            if (now - _dismountSinceMs >= DismountStallMs)
            {
                _dismountSinceMs = 0;
                _fateDropoff = RandomPointInFate(fate);
                _climbingOut = false;
                _landOnDropoff = true;
                Diag("Movement", "cantland", $"still mounted {DismountStallMs / 1000}s after arriving at '{fate.Name}' -> landing at {_fateDropoff} instead");
                return;
            }
            StatusText = $"Arrived at fate: {fate.Name} (dismounting)";
            Features.MountManager.Dismount(); // throttled internally; re-checked next tick
            return;
        }
        _dismountSinceMs = 0;
        _landOnDropoff = false;

        // Grounded -> begin the fate.
        if (C.AutoLevelSync) SyncToFate();
        Stats.OnFateAttempted();
        Features.ChocoboManager.SampleXpAtFateStart();
        // BMR's AI (dodging) comes on now, the rotation only once there is something of the fate's
        // to fight. A fate that hasn't started has no mobs, so an active rotation's auto-target
        // grabs whatever hostile happens to be nearby while we walk to the start NPC.
        SetAiActive(true);
        SetRotationActive(!FateSelector.IsPreparing(fate));
        State = FarmState.InFate;
    }

    private static readonly Random _fateRng = new();

    /// <summary>A random point inside the fate ring (within ~75% of the radius so we stay
    /// comfortably inside). Used as the dropoff target instead of the (often-unreachable) center.</summary>
    private static Vector3 RandomPointInFate(IFate fate)
    {
        // Pick a random X/Z inside the ring and resolve the ACTUAL landable floor there. We do NOT
        // keep the ring-center Y: on sloped/multi-level terrain that height is wrong for the chosen
        // X/Z and is exactly what made us aim into the floor. SnapToFloor searches downward from high
        // above and returns a LANDABLE point only, so every candidate is a real spot we can stand on.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var r = fate.Radius * 0.75f * MathF.Sqrt((float)_fateRng.NextDouble());
            var theta = (float)(_fateRng.NextDouble() * Math.PI * 2);
            var xz = new Vector3(fate.Position.X + r * MathF.Cos(theta), fate.Position.Y,
                                 fate.Position.Z + r * MathF.Sin(theta));

            if (SnapLandable(xz) is { } pt
                && Vector3.Distance(new Vector3(pt.X, 0, pt.Z),
                                    new Vector3(fate.Position.X, 0, fate.Position.Z)) <= fate.Radius)
                return pt;
        }

        // Nothing landable sampled -> snap the ring centre to the floor (or use it as-is).
        return SnapLandable(fate.Position) ?? fate.Position;
    }

    /// <summary>Project a point onto a LANDABLE navmesh floor. Searches downward from well above so
    /// we hit the top surface, and NEVER accepts an unlandable poly (so we never drop somewhere we
    /// can't stand). Returns null if no landable floor is found.</summary>
    private static Vector3? SnapLandable(Vector3 p)
        => Autofate.IPC.NavmeshIPC.PointOnFloor(new Vector3(p.X, p.Y + 50f, p.Z), false, 5f);
    private bool IsInsideFate(IFate fate)
    {
        var me = Player.Object;
        if (me == null) return false;
        return Vector3.Distance(me.Position, fate.Position) <= fate.Radius;
    }

    /// <summary>True if we're physically standing inside any currently-running fate's ring.</summary>
    private static bool IsInsideAnyRunningFate() => FateSelector.GetCurrentFate() != null;

    // Tracks the fate-start NPC interaction so we don't spam-interact while dialogue is up.
    private long _fateNpcInteractedMs;
    // After firing a collect interact (NPC turn-in OR ground pickup) we latch a cooldown and the
    // object id we interacted with. The game opens addons / starts cast animations on a SERVER
    // ROUNDTRIP, so the addon isn't visible on the next tick — if we re-fire the interact in that
    // gap we CANCEL the in-flight one (the Request window opens then instantly closes, forever).
    // This is exactly the guard the reference executor uses (don't re-interact while a previous
    // interact is still resolving). We hold off re-interacting the SAME object until this expires.
    private long _collectInteractCooldownMs;
    private ulong _collectInteractObjId;
    private const long CollectInteractCooldownMs = 2000;
    // Collect delivery run: where we're running to shake the mobs off before a hand-in, when that
    // run gives up, and until when we've stopped trying (so a mob that won't leash can't loop us).
    private Vector3? _collectShedPoint;
    private long _collectShedDeadlineMs;
    private long _collectShedBlockedUntilMs;
    // The mob we stopped to finish off on the way to the NPC, and when we give up on it.
    private ulong _collectFinishTargetId;
    private long _collectFinishDeadlineMs;
    private ushort _startedFateId; // fate we've already done the start-NPC talk for (don't repeat)
    private long _fateStartConfirmedMs;
    // After a FATE completes, MANY fates immediately spawn a chained replacement at (or near) the
    // same spot. Don't instantly commit to navigating off to a far fate — hold briefly so a nearby
    // replacement can spawn and be picked (it'll be the closest). A nearby fate is taken at once.
    private long _postFateGraceUntilMs;
    private const long PostFateGraceMs = 5000;     // how long to wait for a replacement after a clear
    private const float PostFateNearbyDist = 50f;  // a fate within this range is taken immediately (no grace wait)
    private ulong _escortNpcId; // cached escort NPC object id (FateId can flicker to 0 mid-fate)
    private bool _escortChasing; // hysteresis: are we currently chasing the escort NPC?
    // ESCORT detection by RING MOVEMENT: an escort fate's ring (fate.Position) MOVES as the escorted
    // NPC walks; a normal fate's ring is fixed. We sample the position on first sight and flag the
    // fate as a follow fate once the ring has drifted past a threshold. This is reliable where the
    // sheet Rule and MotivationNpc are not (many non-escort fates expose a MotivationNpc).
    private Vector3 _fateInitialPos; // first-seen ring center for the current fate
    private bool _fatePosSampled;    // have we sampled _fateInitialPos yet?
    private bool _ringMovedFollow;   // ring has moved enough -> treat as follow fate (latched)
    private Vector3? _fateDropoff;   // randomized dropoff spot inside the current fate ring
    private bool _climbingOut;
    private bool _dropoffSafetyChecked; // Safe style re-picked the dropoff clear of hostiles (see TickTravelingToFate)
    private const float SafeLandingCheckRange = 80f; // beyond the ring: close enough for its mobs to be loaded       // under the floor at the dropoff: flying up and around before landing
    // Arrived but still mounted since (0 = not waiting), and whether that sent us to land on the
    // dropoff instead of where we first arrived (see ArriveAtFate).
    private long _dismountSinceMs;
    private bool _landOnDropoff;
    // Descending from a hover takes a second or two; past this we can't land where we are.
    private const long DismountStallMs = 5000;
    private const float RingMoveFollowThreshold = 8f; // yalms of ring drift to call it an escort
    private long _dismountedForNpcMs; // when we dismounted to talk to a fate NPC (settle delay)

    /// <summary>A fate dialogue (the Talk window or the start Yes/No) is up and ready for input.</summary>
    private static bool FateDialogueOpen()
        => ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("Talk", out var talk) && ECommons.GenericHelpers.IsAddonReady(talk)
        || ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectYesno", out var yn) && ECommons.GenericHelpers.IsAddonReady(yn);

    /// <summary>
    /// Some fates require talking to a start NPC (orange "!") to begin: it pops Talk dialogue then a
    /// Yes/No to start. TextAdvance (taken session-wide at Start) advances/confirms ALL of that for
    /// us; we only have to do the interact. Returns true while we're handling it (caller should
    /// return and let it finish). The manual Talk/Yes clicking below is a FALLBACK only for when
    /// TextAdvance isn't installed.
    /// </summary>
    private unsafe bool TryStartFateViaNpc(IFate fate)
    {
        // 1) Dialogue handling. When TextAdvance holds control it advances the Talk window and
        //    confirms the Yes/No start prompt itself — we just record the confirm time and wait.
        var yesOpen = ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(
                "SelectYesno", out var yn) && ECommons.GenericHelpers.IsAddonReady(yn);
        var talkOpen = ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(
                "Talk", out var talk) && ECommons.GenericHelpers.IsAddonReady(talk);

        // ALWAYS click the fate-start Yes/No ourselves. TextAdvance does NOT confirm this box — it's
        // a generic FATE-start level-warning prompt ("Get stabbed? — recommended level 94"), the same
        // class of arbitrary Yes/No TA ignores (like the gemstone-exchange box). If we deferred it to
        // TA it would sit open forever. Gate on IsAddonReady (NOT just IsVisible): during the
        // open/close animation the addon is visible but its YesButton pointer is still null (NREs).
        if (yesOpen)
        {
            if (EzThrottler.Throttle("AF_FateYes", 600))
            {
                try { new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectYesno((nint)yn).Yes(); }
                catch (Exception e) { Svc.Log.Verbose($"[Controller] FateYes failed (addon mid-transition): {e.Message}"); }
                _fateStartConfirmedMs = Environment.TickCount64; // fate activates ~3s after this
            }
            return true;
        }
        // Talk window: let TextAdvance advance it when it holds control; otherwise click it ourselves.
        if (talkOpen)
        {
            if (!TextAdvanceIPC.ControlActive && EzThrottler.Throttle("AF_FateTalk", 250))
            {
                try { new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Talk((nint)talk).Click(); }
                catch (Exception e) { Svc.Log.Verbose($"[Controller] FateTalk failed (addon mid-transition): {e.Message}"); }
            }
            return true;
        }

        Diag("NPC", "dialogue", $"yesOpen={yesOpen} talkOpen={talkOpen} taControl={TextAdvanceIPC.ControlActive}");

        // 3) No dialogue open. Look for the fate-start NPC and interact with it. Pass the fate
        // radius so we can also match an un-started "!" NPC (FateId=0) standing in the ring.
        var npcRadius = fate.Radius > 0 ? fate.Radius : 0f;
        var npc = FateTargeting.FindFateStartNpc(_targetFateId, npcRadius);
        if (npc == null) { Diag("NPC", "find", $"no start NPC found (fateId={_targetFateId} radius={npcRadius:F1}) -> treating as combat fate"); return false; }

        // GROUNDED GUARD: land + dismount before targeting/interacting with the start NPC. We don't
        // want to dive at the quest giver from the air or fire Interact while mounted.
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            Navigator.Stop();
            Features.MountManager.Dismount();
            _dismountedForNpcMs = Environment.TickCount64; // start post-dismount settle timer
            StatusText = $"Landing/dismounting before talking to {npc.Name}";
            return true;
        }

        // SETTLE after dismount: dismounting plays a short landing/jump animation. Interacting too
        // early throws "cannot execute action while jumping", so wait ~1s and until we're not
        // jumping/occupied before targeting + interacting.
        if (_dismountedForNpcMs != 0 && Environment.TickCount64 - _dismountedForNpcMs < 1000)
        {
            StatusText = $"Settling before talking to {npc.Name}";
            return true;
        }
        if (Player.IsJumping)
        {
            StatusText = $"Waiting to land before talking to {npc.Name}";
            return true;
        }

        // Walk to the NPC if out of interact range.
        var me = Player.Object;
        if (me == null) return false;
        var dist = Vector3.Distance(me.Position, npc.Position);
        Diag("NPC", "approach", $"npc='{npc.Name}' dist={dist:F1} occupied={ECommons.GenericHelpers.IsOccupied()} animLock={Player.IsAnimationLocked} interactable={Player.Interactable}");
        if (dist > 4f)
        {
            if (!ECommons.GenericHelpers.IsOccupied())
                Navigator.MoveTo(C, npc.Position, 3f, allowMount: false);
            StatusText = $"Approaching fate NPC: {npc.Name}";
            return true;
        }

        // In range: target + interact (throttled).
        Navigator.Stop();
        if (Player.IsAnimationLocked || !Player.Interactable) { Diag("NPC", "interact", $"in range but blocked (animLock={Player.IsAnimationLocked} interactable={Player.Interactable})"); return true; }
        if (Svc.Targets.Target?.GameObjectId != npc.GameObjectId)
            Svc.Targets.Target = npc;
        if (EzThrottler.Throttle("AF_FateInteract", 1500))
        {
            StatusText = $"Starting fate via NPC: {npc.Name}";
            Diag("NPC", "interact", $"FIRING InteractWithObject on '{npc.Name}' (id={npc.GameObjectId})");
            FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()
                ->InteractWithObject(((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address), false);
            _fateNpcInteractedMs = Environment.TickCount64;
            _startedFateId = _targetFateId; // remember we've talked to this fate's start NPC
        }
        return true;
    }

    private void SyncToFate()
    {
        // Never sync while mounted/airborne — be fully grounded before doing anything in a fate.
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying) return;
        if (!EzThrottler.Throttle("AF_FateSync", 1000)) return;
        try
        {
            var fm = FateManager.Instance();
            if (fm == null) return;
            // Only sync if not already synced (LevelSync toggles).
            if (fm->SyncedFateId == 0)
                fm->LevelSync();
        }
        catch (Exception e) { Svc.Log.Verbose($"[Controller] SyncToFate failed: {e.Message}"); }
    }

    // ---------------------------------------------------------------- in-fate
    private void TickInFate()
    {
        var fate = FateSelector.GetFateById(_targetFateId);
        if (fate == null || fate.State == FateState.Ended || fate.State == FateState.Failed)
        {
            OnFateFinished();
            return;
        }

        // GROUNDED GUARD (covers everything below: sync, NPC-start, collect, and combat). Be fully
        // off the mount and on the ground before doing ANYTHING in a fate. Only exception is when a
        // dialogue addon is already up — we still click through that so we don't get stuck.
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            var dlgUp = FateDialogueOpen();
            if (!dlgUp)
            {
                var meM = Player.Object;
                var nowM = Environment.TickCount64;

                // Track how long we've been mounted + stationary here. If we landed on a spot we
                // can't actually dismount on, re-roll a new LANDABLE point and navigate to it.
                if (_inFateMountMs == 0) { _inFateMountMs = nowM; _inFateMountPos = meM?.Position ?? default; }
                else if (meM != null && Vector3.Distance(meM.Position, _inFateMountPos) >= FateStuckMinMove)
                {
                    _inFateMountMs = nowM; _inFateMountPos = meM.Position; // moving (descending/repositioning) -> keep waiting
                }
                else if (nowM - _inFateMountMs >= FateStuckWindowMs)
                {
                    _fateDropoff = RandomPointInFate(fate);
                    _inFateMountMs = nowM; _inFateMountPos = meM?.Position ?? default;
                    Navigator.Stop();
                    Navigator.MoveTo(C, _fateDropoff.Value, 4f, allowMount: false);
                    Diag("Movement", "infateunlandable", $"mounted+stationary in fate >{FateStuckWindowMs}ms -> new landable point {_fateDropoff}");
                    return;
                }

                Navigator.Stop();
                Features.MountManager.Dismount();
                StatusText = $"Landing/dismounting before engaging: {fate.Name}";
                return;
            }
            _inFateMountMs = 0; // dialogue up -> not the stuck case
        }
        else
        {
            _inFateMountMs = 0; // DISMOUNTED in the fate => we made it. Clear the recovery sampler.
        }

        // NOT STARTED YET. A preparing fate is one waiting for a player to talk to its "!" start
        // NPC: it is on the map and has a ring, but no mobs and no timer until someone does, and it
        // sits like that indefinitely (Pearls Apart, The Seashells He Sells, Where Has the Dagon,
        // Coral Support all do). Waiting it out just burns two minutes and walks away from a fate
        // we could have run in full, so find the NPC and start it ourselves. Only when there is no
        // start NPC in sight do we hold the spawn point in case it goes live on its own, bounded.
        if (FateSelector.IsPreparing(fate))
        {
            // No fate mobs yet: keep the rotation from picking a fight with whatever is nearby.
            // Unless something is already hitting us: the NPC won't talk to us in combat anyway.
            SetRotationActive(FateTargeting.GetEnemiesAttackingMe().Count > 0);
            var nowPrep = Environment.TickCount64;
            if (_preparingSinceMs == 0) _preparingSinceMs = nowPrep;
            var waited = (nowPrep - _preparingSinceMs) / 1000;

            var dlgOpen = FateDialogueOpen();
            var talked = _startedFateId == _targetFateId;
            // Interact fired / Yes clicked: the Talk addon takes a server roundtrip to appear and
            // the fate flips to Running a few seconds after the Yes. Hold still meanwhile — moving
            // cancels the pending interaction.
            var settling = talked && (nowPrep - _fateNpcInteractedMs < NpcStartRetryMs
                                      || nowPrep - _fateStartConfirmedMs < 5000);
            if (dlgOpen || !settling)
            {
                // Talked, no dialogue, still preparing: the talk did not take. Try again, a few times.
                if (!dlgOpen && talked)
                {
                    if (_npcStartAttempts >= NpcStartMaxAttempts)
                    {
                        _preparingGaveUp.Add(_targetFateId); // don't re-pick it until it actually starts
                        AbandonFate($"fate '{fate.Name}' did not start after {_npcStartAttempts} talks with its start NPC");
                        return;
                    }
                    Diag("NPC", "retry", $"fate {_targetFateId} '{fate.Name}' still preparing after talk #{_npcStartAttempts} -> talking again");
                    _startedFateId = 0;
                }
                var interactedBefore = _fateNpcInteractedMs;
                var handled = TryStartFateViaNpc(fate);
                if (_fateNpcInteractedMs != interactedBefore) _npcStartAttempts++;
                if (handled) return;
            }
            else
            {
                Navigator.Stop();
                StatusText = $"Starting fate: {fate.Name} (waiting for dialogue)";
                return;
            }

            // No start NPC in sight. Hold the spawn point in case the fate goes live on its own.
            if (nowPrep - _preparingSinceMs >= PreparingWaitMs)
            {
                _preparingGaveUp.Add(_targetFateId); // don't re-pick it until it actually starts
                AbandonFate($"fate '{fate.Name}' still hasn't started after {waited}s and has no start NPC");
                return;
            }

            Navigator.Stop();
            Diag("Fate", "preparing", $"fate {_targetFateId} '{fate.Name}' still preparing, no start NPC found ({waited}s waited)");
            StatusText = $"Waiting for FATE to start: {fate.Name} ({waited}s)";
            return;
        }
        _preparingSinceMs = 0;

        var type = FateSelector.Classify(fate);
        StatusText = $"In fate: {fate.Name} ({type}) {fate.Progress}%";

        // FATE-START NPC: some fates (escort, many "guard"/defend fates, AND collect fates) only
        // begin once you talk to a start NPC carrying the orange "!". Classification (Rule/icon) is
        // unreliable and often tags these as Battle, so DON'T gate solely on type. Trigger NPC-start
        // while the fate hasn't progressed and there are no fate enemies to fight yet — the
        // unambiguous "waiting for you to start it" state.
        //
        // COLLECT fates use the SAME "!" NPC for both starting AND turning in, so we only do the
        // start-talk on the initial join: 0 progress AND we hold none of the collectable yet. Once
        // we've started/collected, HandleCollectFate drives the turn-ins instead.
        var dialogueOpen = FateDialogueOpen();
        var notStarted = fate.Progress <= 0 && FateTargeting.CountFateEnemies(_targetFateId) == 0;
        if (type == FateType.Collect)
        {
            // Only intercept for the initial join. If a turn-in dialogue is mid-flow we still let it
            // pass through here (clicking Talk/Yes is the same), but we must NOT block once items
            // exist or progress has begun — that's handled by HandleCollectFate.
            var collectItemId = FateSelector.GetCollectItemId(fate);
            var haveItems = collectItemId != 0 && InventoryUtil.GetItemCount(collectItemId) > 0;
            // Only on the very first join, and only if we haven't already talked to this fate's
            // start NPC (the latch stops the accept->talk->accept loop).
            var freshJoin = fate.Progress <= 0 && !haveItems && _startedFateId != _targetFateId;
            if ((freshJoin || dialogueOpen) && TryStartFateViaNpc(fate)) return;
        }
        else
        {
            // Latch prevents re-talking to a start NPC we've already engaged (accept loop).
            var canStart = notStarted && _startedFateId != _targetFateId;
            // Same as preparing: nothing of the fate's to fight until the NPC starts it.
            if (canStart || dialogueOpen) SetRotationActive(FateTargeting.GetEnemiesAttackingMe().Count > 0);
            if ((canStart || dialogueOpen) && TryStartFateViaNpc(fate)) return;
        }

        // POST-INTERACT HOLD: after we've fired the interact on the start NPC, the Talk addon takes
        // a few ticks to actually appear. During that window `dialogueOpen` is still false and the
        // start latch is already set, so without this guard we'd fall through to Navigator.MoveTo
        // below — and MOVING cancels the pending interaction ("event canceled", dialogue flickers
        // open then vanishes). So: once we've interacted, STAND STILL until the dialogue opens or
        // the fate actually starts (or a timeout), letting TryStartFateViaNpc drive it next tick.
        if (_fateNpcInteractedMs != 0
            && _startedFateId == _targetFateId
            && fate.Progress <= 0
            && fate.State != FateState.Running
            && Environment.TickCount64 - _fateNpcInteractedMs < 5000)
        {
            Navigator.Stop();
            StatusText = $"Starting fate: {fate.Name} (waiting for dialogue)";
            return;
        }

        // ENEMY NAV HAS ABSOLUTE PRIORITY once inside a fate. If there's any fate enemy to fight
        // (a fresh nearest one OR our sticky engaged target), DO NOT re-center to the ring point —
        // EnsureCombatEngaged owns all movement and walks us to the enemy, however far. Re-centering
        // here is what rubber-banded us back to the middle mid-chase. We only re-center to find
        // action when there is genuinely nothing to fight.
        var hasFateEnemy = FateTargeting.GetNearestFateEnemy(_targetFateId) != null
            || (_engagedTargetId != 0
                && Svc.Objects.SearchById(_engagedTargetId) is IBattleNpc eng
                && FateTargeting.IsFateEnemy(eng, _targetFateId));
        if (!BmrMovementActive() && !hasFateEnemy && !IsInsideFate(fate) && !ECommons.GenericHelpers.IsOccupied())
            Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.6f), allowMount: false);

        // ESCORT DETECTION by RING MOVEMENT. An escort/follow fate's ring center (fate.Position)
        // MOVES as the escorted NPC walks its route; a normal fate's ring is fixed. The sheet Rule
        // is unreliable ("The Ceruleum Road" is a follow fate but classifies as Battle/Rule=3), and
        // MotivationNpc is NOT a follow signal (many non-escort fates expose one — e.g. the
        // Twin-tongued Addison fate). So we detect by watching the ring drift:
        //   - sample the ring center the first time we see this fate;
        //   - once it has moved past RingMoveFollowThreshold, latch this fate as a follow fate.
        // A fate explicitly classified Escort is always a follow fate immediately.
        if (!_fatePosSampled)
        {
            _fateInitialPos = fate.Position;
            _fatePosSampled = true;
        }
        else if (!_ringMovedFollow && Vector3.Distance(_fateInitialPos, fate.Position) >= RingMoveFollowThreshold)
        {
            _ringMovedFollow = true;
            Diag("Escort", "ringmove", $"ring moved {Vector3.Distance(_fateInitialPos, fate.Position):F1}y -> follow fate {_targetFateId}");
        }

        var isFollowFate = type == FateType.Escort || _ringMovedFollow;

        switch (type)
        {
            case FateType.Collect:
                HandleCollectFate(fate);
                break;
            default:
                // Started (or never needed its NPC): now there is something to fight.
                SetRotationActive(true);
                if (isFollowFate) { HandleEscortFate(fate); break; }
                // Battle / Boss / Defend: combat backend does the work. We just make sure we have a target.
                EnsureCombatEngaged(fate);
                break;
        }

        // Fate completed by progress.
        if (fate.Progress >= 100)
            OnFateFinished();
    }

    // The fate enemy we've committed to killing. STICKY: we keep this target until it dies or
    // becomes invalid (this is the single-source-of-truth that the reference AutoTarget uses — its
    // default retarget rule is "only switch if you have no target / are targeting an ally"). The old
    // code re-picked the target in three different places with three different rules every tick,
    // which is exactly why it flickered between targets and sometimes stood still pointing at one.
    private ulong _engagedTargetId;
    // Sticky mass-pull body-pull target: the un-aggroed mob we're currently walking to in order to
    // pull its aggro. Kept until it's on us / dies / leaves, so the move target can't oscillate.
    private ulong _massPullTargetId;

    // Stray aggro taken while we're in a fate: the non-fate mob we've stopped to kill, when we give
    // up on it, and the ones we've already given up on (so the pick can't loop straight back to a
    // mob we just wrote off). Cleared per fate.
    private ulong _strayTargetId;
    private long _strayDeadlineMs;
    private readonly HashSet<ulong> _strayWrittenOff = new();
    // Long enough for a level-appropriate ambient mob, short enough that something we can't kill
    // (out of reach, healing, far above our level) doesn't hold the fate hostage.
    private const long StrayFightTimeoutMs = 30000;

    private void EnsureCombatEngaged(IFate fate)
    {
        var me = Player.Object;
        if (me == null) return;

        // GROUNDED GUARD: never target / engage while mounted or in the air. The combat backend
        // can't act while mounted, and targeting mid-flight makes us dive at mobs from above. Land
        // and dismount FIRST, then bail this tick — we re-evaluate targeting once we're on foot.
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            Diag("Combat", "grounded", $"mounted/flying -> dismounting before engage (mounted={Features.MountManager.IsMounted} flying={Features.MountManager.IsFlying})");
            Navigator.Stop();
            Features.MountManager.Dismount();
            StatusText = $"Landing/dismounting before engaging: {fate.Name}";
            return;
        }

        // BMR AOE-DODGE YIELD — ONLY while actually IN COMBAT. This latch hands movement to BMR so
        // vnav and BMR don't fight while dodging. But it must NEVER block us from APPROACHING mobs:
        // a forbidden zone (e.g. a fire on the ground) sets DangerPresent()=true even when we're far
        // away and out of combat, and gating on it there froze us in place staring at distant mobs
        // (the recurring "stuck not navigating" bug). So we only yield once we're engaged.
        if (BmrMovementActive() && InCombat())
        {
            var now = Environment.TickCount64;
            if (IPCManager.YieldMovementForDodge() || IPCManager.DangerPresent())
            {
                Diag("Combat", "yield", $"yielding movement to BMR (dodge={IPCManager.YieldMovementForDodge()} danger={IPCManager.DangerPresent()})");
                _yieldUntilMs = now + YieldSettleMs;
                Navigator.Stop();
                return;
            }
            if (now < _yieldUntilMs)
            {
                Diag("Combat", "yield", $"in post-dodge settle window ({_yieldUntilMs - now}ms left)");
                Navigator.Stop();
                return;
            }
        }

        // ---- SINGLE TARGET SELECTION (sticky) --------------------------------------------------
        // Exactly ONE place picks the combat target, and it is sticky: keep the current engaged
        // target while it's still a live enemy in OUR fate. Only re-pick when it's gone/invalid.
        //
        var combatTarget = SelectCombatTarget(fate);

        Diag("Combat", "select", $"selected={(combatTarget?.Name.ToString() ?? "<null>")} engagedId={_engagedTargetId} fateEnemies={FateTargeting.CountFateEnemies(_targetFateId)} fateId={_targetFateId} curTarget={(Svc.Targets.Target?.Name.ToString() ?? "<null>")}");

        if (combatTarget == null)
        {
            // No fate mobs right now. Drift toward the fate centre to find action (unless BMR drives
            // movement, in which case it'll reposition us itself).
            _engagedTargetId = 0;
            _massPullTargetId = 0;
            // Safe is holding off on purpose (in combat / low HP): stay put and keep its status.
            if (_safeHolding) { _safeHolding = false; return; }
            Diag("Combat", "notarget", $"no fate enemy; driftToCenter={(!BmrMovementActive() && Vector3.Distance(me.Position, fate.Position) > fate.Radius * 0.4f)} distToCenter={Vector3.Distance(me.Position, fate.Position):F1} radius={fate.Radius:F1}");
            if (!BmrMovementActive() && Vector3.Distance(me.Position, fate.Position) > fate.Radius * 0.4f)
                Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.4f), allowMount: false);
            StatusText = $"In fate: {fate.Name} (waiting for mobs)";
            return;
        }

        // Commit the target ONCE (only write Svc.Targets.Target when it actually changes — redundant
        // writes every tick are part of what made the game UI flicker).
        _engagedTargetId = combatTarget.GameObjectId;
        if (Svc.Targets.Target is not IBattleNpc cur || cur.GameObjectId != combatTarget.GameObjectId)
            Svc.Targets.Target = combatTarget;

        // ---- SAFE: KITE IT OUT OF ITS PACK -----------------------------------------------------
        // Not for stray aggro or a Forlorn: those we walk straight at and kill.
        var kiteable = combatTarget.GameObjectId != _strayTargetId && !FateTargeting.IsForlorn(combatTarget);
        if (kiteable && FateTargeting.EffectivePullStyle(C) == Logic.PullStyle.Safe && TickKite(me, combatTarget))
            return;
        if (_kiteTargetId != 0 && _kiteTargetId != combatTarget.GameObjectId) ClearKite();

        // ---- MOVE TARGET (may differ from combat target for mass-pull body-pulling) ------------
        // The thing we WALK to. Normally the combat target. For mass pull, while under the pile cap,
        // we walk to the nearest un-aggroed fate mob to body-pull it — WITHOUT changing the combat
        // target (so the rotation keeps killing one mob while we gather more). Decoupling these is
        // what lets us "pull n, aoe, repeat" without the target thrashing.
        //
        // NOT while we're clearing stray aggro: body-pulling would walk us away from the mob we
        // stopped to kill and add more fate mobs on top of it, which is the opposite of getting
        // back to the fate quickly. Same for a Forlorn: we walk straight at it and kill it, we do
        // not go collect the rest of the fate on the way.
        var focused = (_strayTargetId != 0 && combatTarget.GameObjectId == _strayTargetId)
                      || FateTargeting.IsForlorn(combatTarget);
        var moveTarget = combatTarget;
        if (FateTargeting.EffectivePullStyle(C) == Logic.PullStyle.Yolo && !focused)
        {
            // One snapshot of the fate's mobs: who is on us decides both the pile size and, via
            // MassPull.PickNext, which mob (if any) we walk to next.
            var myId = me.GameObjectId;
            var chocoId = FateTargeting.GetChocoboId();
            var fateMobs = FateTargeting.GetFateEnemies(_targetFateId);
            var snapshot = fateMobs
                .Select(e => new Logic.MassPull.Enemy(e.GameObjectId, e.Position, FateTargeting.IsAggroedOnUs(e, myId, chocoId)))
                .ToList();
            var aggroed = snapshot.Count(e => e.OnUs);
            if (aggroed < C.MassPullMaxPile)
            {
                // STICKY pull target: keep walking to the SAME un-aggroed mob until it's actually on
                // us (or dies/leaves), then pick the next. Re-picking nearest every tick would thrash
                // when two candidates are similar distance. Sticky here mirrors the sticky combat
                // target and is the other half of the anti-oscillation fix. PickNext also keeps us
                // near the pile we already have (see MassPullRadius).
                var pickId = Logic.MassPull.PickNext(me.Position, snapshot, C.MassPullRadius, _massPullTargetId);
                if (pickId != _massPullTargetId)
                    Diag("Combat", "pull", $"body-pull target -> {pickId?.ToString() ?? "none"} (pile={aggroed}/{C.MassPullMaxPile} radius={C.MassPullRadius:F0})");
                _massPullTargetId = pickId ?? 0;
                var pull = pickId is { } id ? fateMobs.FirstOrDefault(e => e.GameObjectId == id) : null;
                if (pull != null) moveTarget = pull;
            }
            else _massPullTargetId = 0; // pile full -> stop body-pulling
        }
        else _massPullTargetId = 0;

        // ---- MOVEMENT --------------------------------------------------------------------------
        // ALWAYS path to the move target when out of range — fate enemies must be reached at ANY
        // distance. No IsOccupied / throttle gates here (those caused the "targets a mob but won't
        // walk to it" stall).
        var dist = Vector3.Distance(me.Position, moveTarget.Position);
        var engageRange = Math.Max(2.5f, moveTarget.HitboxRadius + 2.5f);
        // Does the mob we're walking to actually have us? This decides who drives movement below,
        // and it is the single most useful thing in this log line when we end up standing still.
        var targetHasUs = FateTargeting.IsAggroedOnUs(
            moveTarget, me.GameObjectId, FateTargeting.GetChocoboId());

        Diag("Combat", "move", $"target='{combatTarget.Name}' move='{moveTarget.Name}' dist={dist:F1} engageRange={engageRange:F1} outOfRange={dist > engageRange} targetHasUs={targetHasUs} bmrMove={BmrMovementActive()} inCombat={InCombat()} navRunning={Autofate.IPC.NavmeshIPC.IsRunning()} navPathing={Autofate.IPC.NavmeshIPC.PathfindInProgress()} meshReady={Autofate.IPC.NavmeshIPC.MeshReady()} myPos={me.Position} targetPos={moveTarget.Position}");

        if (dist > engageRange)
        {
            // OUT OF RANGE.
            if (BmrMovementActive())
            {
                // CRITICAL (AOE-dodge fix): only steal movement from BMR to APPROACH while it has
                // nothing to chase. BMR won't chase an un-aggroed mob, so we vnav in to start the
                // pull — but once the mob is on us, BMR both chases it AND dodges AOEs. If we keep
                // yanking movement back based on distance, the instant BMR steps us out of an AOE
                // we'd be "out of range" and vnav would drag us right back in, looping us back and
                // forth at engage range instead of dodging. So once it is on us we hand movement
                // fully to BMR and do NOT path ourselves.
                //
                // The question is whether THIS mob has us, not whether we are in combat at all.
                // The bare combat flag also fires for something else entirely — an ambient mob that
                // aggroed us on the way in — and then we'd hand movement to BMR while the mob we
                // want is un-aggroed and far away. BMR has nothing to chase, we've stopped
                // navigating, and the fate runs its clock out with us standing still.
                //
                // BMR does NOT close back in after a dodge, though: nothing in our preset makes it
                // follow the target, so it steps us out of the AOE and leaves us there, and a melee
                // job then spams its ranged GCD from 15y away. When BMR reports its danger state
                // (Reborn), the yield latch above already holds us still while an AOE is up, so
                // getting here means the ground is clear and we walk back in ourselves. BMR keeps
                // movement allowed meanwhile: the moment it wants to dodge again it reports that it
                // is navigating and the latch stops us. Vanilla BossMod reports no danger, so there
                // we cannot tell a dodge from a gap and still leave it all to BossMod.
                if (targetHasUs && IPCManager.BmrReportsDanger)
                {
                    IPCManager.SetBmrMovement(true);
                    Navigator.MoveTo(C, moveTarget.Position, engageRange, allowMount: false);
                    StatusText = $"Fighting {combatTarget.Name} (closing in)";
                }
                else if (targetHasUs)
                {
                    IPCManager.SetBmrMovement(true);  // BMR owns approach + dodge while fighting
                    Navigator.Stop();
                    StatusText = $"Fighting {combatTarget.Name} (BMR repositioning)";
                }
                else
                {
                    IPCManager.SetBmrMovement(false); // not on us yet -> vnav closes in to pull
                    Navigator.MoveTo(C, moveTarget.Position, engageRange, allowMount: false);
                    StatusText = $"Engaging {combatTarget.Name} (moving to {moveTarget.Name})";
                }
            }
            else
            {
                Navigator.MoveTo(C, moveTarget.Position, engageRange, allowMount: false);
                StatusText = $"Engaging {combatTarget.Name} (moving to {moveTarget.Name})";
            }
        }
        else
        {
            if (BmrMovementActive())
                IPCManager.SetBmrMovement(true); // in range -> hand movement back so BMR dodges/fights
            else
                Navigator.Stop();
            // Open combat ourselves so the backend engages even if it's waiting to be hit.
            FateTargeting.StartAutoAttack(combatTarget);
            StatusText = $"Fighting {combatTarget.Name}";
        }
    }

    /// <summary>
    /// Pick the fate combat target, STICKILY (mirrors the reference AutoTarget retarget rule). Order:
    ///   0) A Forlorn (Maiden) in our fate, over anything else at all.
    ///   1) A non-fate mob that is hitting us, so it stops riding us for the rest of the fate.
    ///   2) Keep the currently engaged target if it's still a live enemy in OUR fate.
    ///   3) Otherwise, for Defend/Escort fates, prefer the enemy attacking a protected friendly.
    ///   4) Otherwise the nearest fate enemy.
    /// Returns null when our fate has no live enemies. This single selector replaces the three
    /// conflicting ones the old code ran each tick.
    /// </summary>
    private IBattleNpc? SelectCombatTarget(IFate fate)
    {
        // 0) THE FORLORN. A Forlorn (Maiden) in our fate is worth more than the fate itself (see
        //    FateTargeting.IsForlorn) and it does not wait for us: other players kill it and it
        //    despawns on its own. So it outranks EVERYTHING here, the sticky target and stray aggro
        //    included — whatever we were fighting is still there when it is down.
        var forlorn = FateTargeting.GetNearestForlorn(_targetFateId);
        if (forlorn != null)
        {
            if (_engagedTargetId != forlorn.GameObjectId)
                Diag("Combat", "forlorn", $"'{forlorn.Name}' is up -> dropping everything to kill it");
            return forlorn;
        }

        // 1) STRAY AGGRO: something that is NOT part of our fate is hitting us. Nothing else in this
        //    method will ever pick it — every branch below is fate-scoped, and the fate-scoped BMR
        //    AutoTarget hint keeps the rotation off it as well — so left alone it rides us for the
        //    rest of the fate, interrupting hand-ins and chipping us down with no one answering.
        //    Kill it first; the fate is still there afterwards.
        var stray = SelectStrayAttacker();
        if (stray != null) return stray;

        var safe = FateTargeting.EffectivePullStyle(C) == Logic.PullStyle.Safe;

        // 2) STICKY: keep the engaged target while it's valid (alive + in our fate). This is the
        //    anti-flicker rule — we do NOT yank to a closer mob just because one wandered nearer.
        //    Safe style makes one exception: if a fate mob is hitting us while we're still walking
        //    to a target that isn't, that mob comes first. Walking on would drag it into the next
        //    fight, which is exactly the pile Safe exists to avoid.
        if (safe && NearestFateMobOnUs() is { } onUs
            && !(Svc.Objects.SearchById(_engagedTargetId) is IBattleNpc cur && IsOnUs(cur)))
        {
            if (_engagedTargetId != onUs.GameObjectId)
                Diag("Combat", "safe", $"'{onUs.Name}' is on us -> fighting it before anything new");
            _safeHoldSinceMs = 0;
            return onUs;
        }

        //    Safe: nothing on us, so whatever comes next is a NEW pull (or finishing one we started).
        //    Don't start one while still in combat: mobs an AOE clipped take a moment to turn on us,
        //    and going off to pull something else meanwhile is how one fight becomes three. Nor
        //    while hurt; out of combat HP is back in seconds. Bounded, so a combat flag that never
        //    clears can't park us for good.
        if (safe && NearestFateMobOnUs() == null && !(Svc.Objects.SearchById(_engagedTargetId) is IBattleNpc pulling
                                                       && pulling.GameObjectId == _kiteTargetId && !InCombat()))
        {
            var me = Player.Object;
            var hp = me is { MaxHp: > 0 } ? (float)me.CurrentHp / me.MaxHp : 1f;
            var why = InCombat() ? "still in combat" : !Logic.SafePull.MayStartNewPull(hp) ? $"HP {hp:P0}" : null;
            var now = Environment.TickCount64;
            if (why != null)
            {
                if (_safeHoldSinceMs == 0) _safeHoldSinceMs = now;
                if (now - _safeHoldSinceMs < SafeHoldMaxMs)
                {
                    Diag("Combat", "safehold", $"not starting a new pull: {why}");
                    Navigator.Stop();
                    StatusText = $"Safe: waiting ({why})";
                    _safeHolding = true;
                    return null;
                }
            }
            else _safeHoldSinceMs = 0;
        }

        if (_engagedTargetId != 0
            && Svc.Objects.SearchById(_engagedTargetId) is IBattleNpc engaged
            && FateTargeting.IsFateEnemy(engaged, _targetFateId))
        {
            return engaged;
        }

        // 3) Defend/Escort peel: the enemy actively attacking a protected friendly takes priority
        //    when we don't already have a valid target.
        var type = FateSelector.Classify(fate);
        if (type == FateType.Defend || type == FateType.Escort)
        {
            var threat = FateTargeting.GetActiveDefendThreat(_targetFateId);
            if (threat != null) return threat;
        }

        // 4) Safe: the fate mob with the fewest idle hostiles around it (they'd join in), weighed
        //    against distance. Yolo: simply the nearest, mass pull gathers the rest.
        if (safe) return SelectSafeTarget();
        return FateTargeting.GetNearestFateEnemy(_targetFateId);
    }

    // ---------------------------------------------------------------- safe: kiting
    // Safe style. A mob standing alone is walked up to. A mob with idle company is KITED: we attack
    // it from range where we stand if we can (in range, nothing else close to us), else from a spot
    // Kite.PullRange away from it, clear of every other idle mob, and then let it come to us, away
    // from its pack. Anything already on us is also left to come to us: walking at it walks us into
    // whatever is standing next to it. We only walk to a mob that is on us but isn't coming (ranged
    // mobs, stuck ones).
    private ulong _kiteTargetId;
    private Vector3 _kiteSpot;
    // Safe hold (see SelectCombatTarget): since when we've been waiting before a new pull, and how long at most.
    private long _safeHoldSinceMs;
    private bool _safeHolding; // this tick's "no target" is the Safe hold, not an empty fate
    private const long SafeHoldMaxMs = 10000;
    private long _kiteStartMs, _kiteAtSpotMs;
    private readonly HashSet<ulong> _kiteGaveUp = new(); // mobs we couldn't kite this fate: walk in
    // Getting to the spot and pulling shouldn't take longer than this; past it we walk in instead.
    private const long KiteTimeoutMs = 30000;
    // Standing at the spot without the pull landing (no line of sight, action unusable...).
    private const long KitePullTimeoutMs = 6000;
    private const float KiteSpotReach = 2f;
    // Waiting for a mob on us to come: it has to have closed in by ApproachMinGain within this.
    private ulong _approachId;
    private float _approachBest;
    private long _approachSinceMs;
    private const long ApproachStallMs = 3000;
    private const float ApproachMinGain = 1f;
    // Ranged jobs and healers fight from where they stand while the mob is within this.
    private const float RangedFightRange = 20f;

    private void ClearKite()
    {
        _kiteTargetId = 0;
        _kiteStartMs = 0;
        _kiteAtSpotMs = 0;
        _approachId = 0;
    }

    /// <summary>
    /// Safe-style movement for <paramref name="target"/>. Returns true while it owns movement this
    /// tick; false to engage normally (a lone mob, a mob in reach, one that isn't coming, gave up).
    /// </summary>
    private bool TickKite(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter me, IBattleNpc target)
    {
        var now = Environment.TickCount64;
        var engageRange = Math.Max(2.5f, target.HitboxRadius + 2.5f);
        var job = FateTargeting.PlayerJob();
        var melee = job.Role is 1 or 2;
        var dist = Vector3.Distance(me.Position, target.Position);

        // ON US: let it come. Ranged jobs and healers fight it from here while it's in reach.
        if (IsOnUs(target))
        {
            _kiteTargetId = 0; // pulled (by us or otherwise): the pull part is over
            if (dist <= engageRange) { _approachId = 0; return false; }
            if (!melee && dist <= RangedFightRange)
            {
                Navigator.Stop();
                StatusText = $"Fighting {target.Name} from range";
                return true;
            }
            if (_approachId != target.GameObjectId || _approachBest - dist >= ApproachMinGain)
            {
                _approachId = target.GameObjectId;
                _approachBest = dist;
                _approachSinceMs = now;
            }
            if (now - _approachSinceMs > ApproachStallMs)
            {
                Diag("Combat", "kite", $"'{target.Name}' is on us but isn't coming ({dist:F0}y) -> walking to it");
                return false;
            }
            Navigator.Stop();
            StatusText = $"Kiting {target.Name} (letting it come, {dist:F0}y)";
            return true;
        }
        _approachId = 0;

        if (_kiteGaveUp.Contains(target.GameObjectId)) return false;

        // NEW TARGET: kite only if it has idle company that would join in.
        if (_kiteTargetId != target.GameObjectId)
        {
            var others = FateTargeting.GetIdleHostiles(dist + Logic.SafePull.CrowdRadius * 2)
                .Where(h => h.GameObjectId != target.GameObjectId)
                .Select(h => h.Position)
                .ToList();
            var neighbours = others.Count(o => Vector3.Distance(o, target.Position) <= Logic.SafePull.CrowdRadius);
            if (neighbours == 0) return false; // on its own: just go hit it
            var pullAction = Logic.Kite.RangedPullAction(job.Id);
            if (melee && pullAction == null)
            {
                _kiteGaveUp.Add(target.GameObjectId); // e.g. Monk: nothing to pull with
                return false;
            }

            if (Logic.Kite.CanPullFromHere(me.Position, target.Position, others))
                _kiteSpot = me.Position;
            else
            {
                var spot = Logic.Kite.PullSpot(me.Position, target.Position, others);
                // Put it on the floor we'll be walking on (the target's height is only a guess there).
                _kiteSpot = NavmeshIPC.PointOnFloor(spot + new Vector3(0, 10, 0), true, 5f) ?? spot;
            }
            _kiteTargetId = target.GameObjectId;
            _kiteStartMs = now;
            _kiteAtSpotMs = 0;
            Diag("Combat", "kite", $"'{target.Name}' has {neighbours} idle neighbour(s) -> pulling it from "
                + (_kiteSpot == me.Position ? "here" : $"{_kiteSpot} ({Vector3.Distance(me.Position, _kiteSpot):F0}y away)"));
        }

        if (now - _kiteStartMs > KiteTimeoutMs
            || (_kiteAtSpotMs != 0 && now - _kiteAtSpotMs > KitePullTimeoutMs))
        {
            Diag("Combat", "kite", $"couldn't pull '{target.Name}' from range -> walking in");
            _kiteGaveUp.Add(target.GameObjectId);
            ClearKite();
            return false;
        }

        // GET TO THE SPOT (if we aren't pulling from where we stood).
        if (Vector2.Distance(Flat(me.Position), Flat(_kiteSpot)) > KiteSpotReach)
        {
            StatusText = $"Kiting {target.Name} (moving to pull spot)";
            Navigator.MoveTo(C, _kiteSpot, KiteSpotReach * 0.75f, allowMount: false);
            return true;
        }

        // PULL IT.
        Navigator.Stop();
        if (_kiteAtSpotMs == 0) _kiteAtSpotMs = now;
        StatusText = $"Kiting {target.Name} (pulling)";
        if (EzThrottler.Throttle("AF_KitePull", 1000))
        {
            if (melee) FateTargeting.TryUseAction(Logic.Kite.RangedPullAction(job.Id)!.Value, target);
            else FateTargeting.StartAutoAttack(target);
        }
        return true;
    }

    /// <summary>Is this mob targeting us or our chocobo?</summary>
    private static bool IsOnUs(IBattleNpc mob)
        => Player.Object is { } me && FateTargeting.IsAggroedOnUs(mob, me.GameObjectId, FateTargeting.GetChocoboId());

    /// <summary>Nearest mob of our fate that is targeting us or our chocobo, or null.</summary>
    private IBattleNpc? NearestFateMobOnUs()
        => FateTargeting.GetFateEnemies(_targetFateId).FirstOrDefault(IsOnUs); // nearest-first

    /// <summary>
    /// Safe pull style: the next fate mob to engage when nothing of the fate's is on us, picked by
    /// <see cref="Logic.SafePull.PickTarget"/> (nearest, penalised for idle hostiles around it).
    /// </summary>
    private IBattleNpc? SelectSafeTarget()
    {
        var me = Player.Object;
        if (me == null) return null;
        var fateMobs = FateTargeting.GetFateEnemies(_targetFateId);
        if (fateMobs.Count == 0) return null;
        var candidates = fateMobs.Select(e => new Logic.SafePull.Mob(e.GameObjectId, e.Position)).ToList();
        // Only hostiles near some candidate can matter; the furthest candidate plus the crowd radius bounds it.
        var reach = fateMobs.Max(e => Vector3.Distance(me.Position, e.Position)) + Logic.SafePull.CrowdRadius;
        var idle = FateTargeting.GetIdleHostiles(reach)
            .Select(e => new Logic.SafePull.Mob(e.GameObjectId, e.Position)).ToList();
        var pickId = Logic.SafePull.PickTarget(me.Position, candidates, idle);
        var pick = fateMobs.FirstOrDefault(e => e.GameObjectId == pickId);
        if (pick != null && pick.GameObjectId != _engagedTargetId)
            Diag("Combat", "safe", $"next: '{pick.Name}' ({Vector3.Distance(me.Position, pick.Position):F0}y, {idle.Count} idle hostiles around the fate)");
        return pick;
    }

    /// <summary>
    /// The mob to kill before we can get on with the fate: one that is attacking us (or the
    /// chocobo) and is not part of our fate. Null when there is none.
    ///
    /// Sticky, so we finish the one we started on instead of flipping between two, and bounded: a
    /// mob that will not die inside <see cref="StrayFightTimeoutMs"/> is written off and we go back
    /// to the fate. Writing one off leaves it on us, which is exactly where we were before this
    /// existed — one mob we cannot kill should cost us a few seconds, not the whole run.
    /// </summary>
    private IBattleNpc? SelectStrayAttacker()
    {
        var me = Player.Object;
        if (me == null || _targetFateId == 0) return null;
        var now = Environment.TickCount64;
        var chocoId = FateTargeting.GetChocoboId();

        // Still working on the one we picked?
        if (_strayTargetId != 0)
        {
            if (now >= _strayDeadlineMs)
            {
                Diag("Combat", "stray", $"gave up on stray {_strayTargetId} after {StrayFightTimeoutMs / 1000}s -> back to the fate");
                _strayWrittenOff.Add(_strayTargetId);
                _strayTargetId = 0;
            }
            else if (Svc.Objects.SearchById(_strayTargetId) is IBattleNpc cur
                     && FateTargeting.IsAttackableEnemy(cur)
                     && !FateTargeting.IsFateEnemy(cur, _targetFateId)
                     && FateTargeting.IsAggroedOnUs(cur, me.GameObjectId, chocoId))
            {
                return cur;
            }
            else
            {
                _strayTargetId = 0; // dead, despawned, leashed off, or it belongs to our fate now
            }
        }

        // Pick a new one: nearest thing on us that our fate doesn't own and we haven't written off.
        foreach (var e in FateTargeting.GetEnemiesAttackingMe())
        {
            if (FateTargeting.IsFateEnemy(e, _targetFateId)) continue;
            if (_strayWrittenOff.Contains(e.GameObjectId)) continue;
            _strayTargetId = e.GameObjectId;
            _strayDeadlineMs = now + StrayFightTimeoutMs;
            Diag("Combat", "stray", $"'{e.Name}' is on us and isn't part of the fate -> killing it first");
            return e;
        }
        return null;
    }

    private bool BmrMovementActive() => IPCManager.BmrHandlesMovement(C);

    // ---------------------------------------------------------------- collect fates
    // Collect fates: grab labeled ground items (EventObj carrying the FateId), hand them in to the
    // game-designated objective NPC in fixed batches, and leave once the fate hits 100% (or runs dry
    // of items with progress still incomplete). We don't try to LEARN a per-item progress value
    // (that was the old, never-calibrated WIP path); instead we mirror DWD/BMR: hold until we have a
    // full batch, hand in, repeat — with a final partial hand-in when no more items remain.
    // Collect-fate goal, computed fresh each tick (mirrors DWD's FateUtils.GetGoal). EXACTLY ONE of
    // these drives the tick — that single-owner model is what stops the target tug-of-war that made
    // us flicker between the NPC and an enemy.
    private enum CollectGoal { None, HandIn, Pickup, Fight }

    private CollectGoal GetCollectGoal(IFate fate, uint collectItemId, int have)
    {
        // Nothing to do once the fate is done.
        if (fate.Progress >= 100) return CollectGoal.None;
        if (collectItemId == 0) return CollectGoal.None;

        // A FORLORN OUTRANKS THE COLLECTABLES. The buff it drops pays out on every fate after this
        // one, which is more than any single hand-in is worth, and unlike the items on the ground it
        // will not still be there in a minute. Fight first, gather after.
        if (FateTargeting.GetNearestForlorn(_targetFateId) != null) return CollectGoal.Fight;

        var moreItemsOnGround = FateTargeting.GetNearestCollectable(_targetFateId) != null;

        // LAST PHASE. The fate is only running out its clock now: there is no time left to gather,
        // carry and deliver another batch, and kills on their own pay nothing here. Deliver what we
        // hold (it still counts) and, once our hands are empty, the caller moves us on.
        if (IsCollectEndgame(fate))
            return have > 0 ? CollectGoal.HandIn : CollectGoal.None;

        // CLOSER. What we're carrying already finishes the fate, so it goes in NOW — anything we
        // kill or pick up past this point is thrown away.
        if (have > 0 && have >= CollectItemsToFinish(fate))
            return CollectGoal.HandIn;

        // HAND IN a FULL batch (efficient, contributes to the shared bar).
        if (have >= CollectBatchSize)
            return CollectGoal.HandIn;

        // GATHER while nothing is on us. The items are the objective; mobs only matter because they
        // drop more of them. So with something on the ground and nobody hitting us, grabbing it
        // beats opening a fight we would then have to finish before we could gather again.
        if (moreItemsOnGround && !InCombat() && FateTargeting.GetEnemiesAttackingMe().Count == 0)
            return CollectGoal.Pickup;

        // FIGHT if there are enemies in OUR fate. Others may have already started it and the enemies
        // ARE the work (they drop the collectables) — going to fight them beats trekking to the start
        // NPC or idling. Also covers being kept in combat. FateId-scoped: never foreign-fate mobs.
        if (FateTargeting.GetNearestFateEnemy(_targetFateId) != null || InCombat())
            return CollectGoal.Fight;

        // PICK UP the nearest ground collectable (no enemies around).
        if (moreItemsOnGround)
            return CollectGoal.Pickup;

        // Nothing left to fight or grab but we still hold items -> hand in the partial batch.
        if (have > 0)
            return CollectGoal.HandIn;

        return CollectGoal.None;
    }

    /// <summary>True once a collect fate is just running out its clock (its last phase).</summary>
    private static bool IsCollectEndgame(IFate fate)
    {
        var left = fate.TimeRemaining;
        return left > 0 && left <= CollectEndgameSeconds;
    }

    /// <summary>
    /// How many more hand-ins would close this fate out. The bar is a straight percentage of the
    /// goal and the client counts the hand-ins made so far, so one item is worth
    /// Progress/HandInCount percent. We only trust that where it lands inside the range collect
    /// fates actually use, and otherwise assume the standard 20-item goal. Erring low is cheap (one
    /// extra trip to the NPC, the items still count); erring high is what leaves us fighting a fate
    /// we could have finished.
    /// </summary>
    private static int CollectItemsToFinish(IFate fate)
    {
        var remaining = 100 - fate.Progress;
        if (remaining <= 0) return 0;
        return Math.Max(1, (int)Math.Ceiling(remaining / CollectPercentPerItem(fate)));
    }

    private static float CollectPercentPerItem(IFate fate)
    {
        try
        {
            int handed = fate.HandInCount, progress = fate.Progress;
            if (handed > 0 && progress > 0)
            {
                var perItem = progress / (float)handed;
                if (perItem >= 100f / CollectMaxGoal && perItem <= 100f / CollectMinGoal) return perItem;
            }
        }
        catch { /* HandInCount can throw in some states */ }
        return 100f / CollectDefaultGoal;
    }

    private unsafe void HandleCollectFate(IFate fate)
    {
        // SHARED GOAL: collect fates progress for EVERYONE — anyone turning in items advances the
        // same bar. This method runs every tick, so re-check completion FIRST: the moment progress
        // hits 100 (whether from us or other players) stop and leave instead of doing more work.
        // OnFateFinished routes through ClearingAggro if we're still in combat.
        if (fate.Progress >= 100) { OnFateFinished(); return; }

        var collectItemId = FateSelector.GetCollectItemId(fate);
        var have = collectItemId != 0 ? InventoryUtil.GetItemCount(collectItemId) : 0;

        // TURN-IN DELEGATION (the reference plugins' approach): TextAdvance handles the ENTIRE
        // Request-window flow (fills the slot + clicks Hand Over + skips the Talk dialogue). Our old
        // manual AgentInventoryContext.OpenForItemSlot poke is what made the inventory "pull up then
        // instantly close" — the game discards that synthetic context menu. We just open the turn-in
        // dialogue and let TextAdvance complete it.
        //
        // RE-ASSERT control here every tick (self-healing). TextAdvance can silently drop external
        // control (zone change / its own timeout / reload), and if it has, the Request window sits
        // unfilled forever — the "stuck at turn-in until I restart the plugin" bug. Enable() reconciles
        // against TextAdvance's ACTUAL state and re-issues the request when it isn't in control.
        TextAdvanceIPC.Enable();

        // If the Request window is open, TextAdvance is handling it — keep the combat backend OFF so
        // nothing competes, and just wait for it to finish.
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(
                "Request", out var req) && req != null && req->IsVisible)
        {
            SetCombatBackend(false);
            Diag("Collect", "request", $"Request window open: taInstalled={TextAdvanceIPC.IsInstalled} taControl={TextAdvanceIPC.IsInExternalControl()} item={collectItemId} have={have}");
            // Only trust TextAdvance to drive it if it's ACTUALLY in external control right now. If
            // it isn't (not installed, or dropped control and somehow didn't re-take), fall back to
            // driving the Request window ourselves so we never get stuck with the slot unfilled.
            if (TextAdvanceIPC.IsInstalled && TextAdvanceIPC.IsInExternalControl())
            {
                StatusText = "Collect: handing in (TextAdvance)";
                return;
            }
            HandleRequestWindow((nint)req, fate, collectItemId); // fallback: fill + hand over ourselves
            return;
        }

        var goal = GetCollectGoal(fate, collectItemId, have);
        Diag("Collect", "goal", $"goal={goal} item={collectItemId} have={have} need={CollectItemsToFinish(fate)} progress={fate.Progress} timeLeft={fate.TimeRemaining}s groundItems={(FateTargeting.GetNearestCollectable(_targetFateId) != null)} inCombat={InCombat()} attackers={FateTargeting.GetEnemiesAttackingMe().Count}");

        // SINGLE-OWNER RULE: for every goal EXCEPT Fight, the rotation backend must be OFF. If it's
        // running it will re-target an enemy every frame and fight us for Svc.Targets.Target — that
        // is the "flicks between the NPC and an enemy without doing anything" bug. We only turn the
        // backend on for the Fight goal (something is actively attacking us). HandIn is the one
        // exception to who decides: the delivery run flips the rotation on and off itself for
        // finishing blows, so setting it here too would toggle it twice a tick.
        if (goal != CollectGoal.HandIn)
            SetCombatBackend(goal == CollectGoal.Fight);

        switch (goal)
        {
            case CollectGoal.HandIn:
                CollectHandIn(fate, have);
                return;

            case CollectGoal.Fight:
                // Something is attacking us (or a Forlorn is up) — let the combat path deal with it,
                // then we resume next tick. Leave the status alone when it is the Forlorn: the
                // combat path already says what we are killing and why we stopped gathering.
                EnsureCombatEngaged(fate);
                if (FateTargeting.GetNearestForlorn(_targetFateId) == null)
                    StatusText = "Collect: clearing combat before gathering";
                return;

            case CollectGoal.Pickup:
                CollectPickup(have);
                return;

            default: // None
                // Fate done, or waiting for items to respawn / combat to clear. Don't touch the
                // target (no flicker); just idle near the centre so we're positioned for the next item.
                if (fate.Progress >= 100) { OnFateFinished(); return; }
                // Last phase and our hands are empty: this fate cannot pay us anything more, so
                // stop feeding it kills and go find one that can.
                if (IsCollectEndgame(fate))
                {
                    AbandonFate($"collect fate '{fate.Name}' is in its last phase ({fate.TimeRemaining}s left) with nothing to hand in");
                    return;
                }
                StatusText = $"Collect fate: {fate.Name} {fate.Progress}% (have {have})";
                if (!BmrMovementActive())
                    Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.5f), allowMount: false);
                return;
        }
    }

    /// <summary>
    /// Deliver what we are carrying. This is a committed run, not a stroll: the mobs that were on us
    /// while gathering come with us, and an interact only fires while standing still and not
    /// animation-locked, so a train at the NPC is what makes a hand-in drag on (or kill us holding a
    /// full batch). We play it the way you would by hand — finish off whatever is already dying,
    /// outrun the rest along the spawn-center -> NPC line where fate mobs leash, then turn in clean.
    /// The combat backend is OFF (caller guarantees) apart from those finishing blows, so nothing
    /// competes for the target.
    /// </summary>
    private unsafe void CollectHandIn(IFate fate, int have)
    {
        var npc = FateTargeting.GetCollectTurnInNpc(_targetFateId);
        if (npc == null)
        {
            StatusText = "Collect: looking for turn-in NPC";
            return;
        }

        var me = Player.Object;
        if (me == null) return;

        // WE own movement AND the backend for the whole delivery. BMR's AI would keep repositioning
        // us around the very mobs we are trying to leave behind, which is the opposite of running
        // away in a straight line; the rotation only comes on for the finishing blows below.
        SetAiActive(false);
        if (BmrMovementActive()) IPCManager.SetBmrMovement(false);

        var now = Environment.TickCount64;
        var attackers = FateTargeting.GetEnemiesAttackingMe();

        // FINISH OFF what is already dying: a couple of GCDs removes it for good, whereas outrunning
        // something at 10% HP wastes the kill and dragging it to the NPC just moves the problem.
        var finishable = attackers.FirstOrDefault(e =>
            e.MaxHp > 0 && e.CurrentHp / (float)e.MaxHp <= CollectFinishHpFraction
            && Vector3.Distance(me.Position, e.Position) <= CollectFinishRange);
        // Standing still to kill something only pays off if it dies. If it hasn't by now (out of
        // our reach, healing, whatever), give up on THIS mob and run — trading hits with it in the
        // middle of a delivery is exactly the stall we're trying to get rid of.
        if (finishable != null && finishable.GameObjectId == _collectFinishTargetId && now >= _collectFinishDeadlineMs)
            finishable = null;
        if (finishable != null)
        {
            if (finishable.GameObjectId != _collectFinishTargetId)
            {
                _collectFinishTargetId = finishable.GameObjectId;
                _collectFinishDeadlineMs = now + CollectFinishTimeoutMs;
            }
            Navigator.Stop();
            if (Svc.Targets.Target?.GameObjectId != finishable.GameObjectId)
                Svc.Targets.Target = finishable;
            SetRotationActive(true); // rotation ONLY — BMR's AI stays off so nothing moves us
            FateTargeting.StartAutoAttack(finishable);
            StatusText = $"Collect: finishing {finishable.Name} before turning in";
            return;
        }
        SetRotationActive(false);

        // Mobs on our chocobo don't stop us interacting, so only the ones actually on US count.
        var onUs = attackers.Where(e => e.TargetObjectId == me.GameObjectId).ToList();
        bool Crowded() => onUs.Any(e => Vector3.Distance(me.Position, e.Position) <= CollectQuietRange);

        // SHED RUN in progress: keep running the line until they drop us or we run out of patience.
        if (_collectShedPoint is { } shedPoint)
        {
            if (!Crowded() || now >= _collectShedDeadlineMs)
            {
                Diag("Collect", "shed", Crowded()
                    ? "shed run timed out with mobs still on us -> handing in anyway"
                    : "shed run worked, we're clear -> back to the NPC");
                // One shed run per delivery either way: a mob that won't leash isn't worth the fate.
                _collectShedBlockedUntilMs = now + CollectShedCooldownMs;
                _collectShedPoint = null;
            }
            else
            {
                Navigator.MoveTo(C, shedPoint, 3f, allowMount: false);
                StatusText = $"Collect: shaking off {onUs.Count} mob(s) before turning in";
                return;
            }
        }

        if (Vector3.Distance(me.Position, npc.Position) > 4f)
        {
            Navigator.MoveTo(C, npc.Position, 3f, allowMount: false);
            StatusText = $"Collect: turning in {have} (to {npc.Name})";
            return;
        }

        // At the NPC with a train still on us: shake it before interacting.
        if (now >= _collectShedBlockedUntilMs && Crowded())
        {
            _collectShedPoint = CollectShedPoint(fate, npc);
            _collectShedDeadlineMs = now + CollectShedTimeoutMs;
            Diag("Collect", "shed", $"{onUs.Count} mob(s) on us at the NPC -> running out to {_collectShedPoint}");
            return;
        }

        // Set the NPC target ONCE and keep it (backend is off, so it stays put — no flicker).
        if (Svc.Targets.Target?.GameObjectId != npc.GameObjectId)
            Svc.Targets.Target = npc;
        TryCollectInteract(npc, $"Collect: interacting with {npc.Name}");
    }

    /// <summary>
    /// Where to run to drop the mobs on our back: along the spawn-center -> NPC line and out past
    /// the ring, where fate mobs leash off. Straight out from the center if the NPC stands on it.
    /// </summary>
    private static Vector3 CollectShedPoint(IFate fate, IGameObject npc)
    {
        var dir = npc.Position - fate.Position;
        dir.Y = 0;
        if (dir.LengthSquared() < 1f)
        {
            var me = Player.Object;
            dir = me != null ? me.Position - fate.Position : Vector3.UnitX;
            dir.Y = 0;
            if (dir.LengthSquared() < 1f) dir = Vector3.UnitX;
        }
        return fate.Position + Vector3.Normalize(dir) * (Math.Max(fate.Radius, 10f) + CollectShedMargin);
    }

    /// <summary>Walk to the nearest ground collectable and interact to pick it up. Combat backend is
    /// already OFF (caller guarantees), so nothing competes for the target.</summary>
    private unsafe void CollectPickup(int have)
    {
        var item = FateTargeting.GetNearestCollectable(_targetFateId);
        if (item == null) return;

        var me = Player.Object;
        if (me == null) return;

        if (Vector3.Distance(me.Position, item.Position) > 3f)
        {
            Navigator.MoveTo(C, item.Position, 2f, allowMount: false);
            StatusText = $"Collect: grabbing {item.Name} (have {have})";
            return;
        }

        if (Svc.Targets.Target?.GameObjectId != item.GameObjectId)
            Svc.Targets.Target = item;
        TryCollectInteract(item, $"Collect: grabbing {item.Name} (have {have})");
    }

    /// <summary>
    /// Fire a collect interact (NPC turn-in or ground pickup) the way the reference executor does:
    ///   - STOP moving first (interacting while still pathing cancels the in-flight interact);
    ///   - only when NOT animation-locked and interactable;
    ///   - and ONLY ONCE per object until a cooldown expires, because the addon/cast it triggers
    ///     opens on a server roundtrip and isn't visible next tick. Re-firing in that gap is what
    ///     cancelled the opening Request window and made it open/close in an infinite loop.
    /// </summary>
    private unsafe void TryCollectInteract(IGameObject obj, string status)
    {
        Navigator.Stop(); // critical: never interact while still moving toward the target
        if (Player.IsAnimationLocked || !Player.Interactable)
        {
            Diag("Collect", "interact", $"blocked (animLock={Player.IsAnimationLocked} interactable={Player.Interactable}) obj='{obj.Name}'");
            return;
        }

        var now = Environment.TickCount64;
        // Still within the cooldown for THIS object -> the previous interact is resolving; wait.
        if (_collectInteractObjId == obj.GameObjectId && now < _collectInteractCooldownMs)
        {
            Diag("Collect", "interact", $"cooldown ({_collectInteractCooldownMs - now}ms left) obj='{obj.Name}'");
            StatusText = status + " (waiting)";
            return;
        }

        Diag("Collect", "interact", $"FIRING InteractWithObject on '{obj.Name}' (id={obj.GameObjectId})");
        FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()
            ->InteractWithObject(((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address), false);
        _collectInteractObjId = obj.GameObjectId;
        _collectInteractCooldownMs = now + CollectInteractCooldownMs;
        StatusText = status;
    }

    /// <summary>
    /// Drive the Item Request (collect turn-in) window: fill the slot with the collect item, then
    /// click Hand Over. We use ECommons' Request master for Hand Over + AgentInventoryContext to
    /// fill the slot (right-click the inventory item against the open Request addon).
    /// </summary>
    private unsafe void HandleRequestWindow(nint reqAddon, IFate fate, uint collectItemId)
    {
        // Guard: the Request addon can be visible while its buttons/nodes aren't built yet. Touching
        // rq.IsHandOverEnabled (-> HandOverButton->IsEnabled) on a null button NREs, so verify the
        // addon is fully ready AND the hand-over button exists before reading it.
        if (!ECommons.GenericHelpers.IsAddonReady((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)reqAddon))
            return;

        var rq = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Request(reqAddon);

        bool handOverReady;
        try { handOverReady = rq.HandOverButton != null && rq.IsHandOverEnabled; }
        catch (Exception e) { Svc.Log.Verbose($"[Collect] Request not ready: {e.Message}"); return; }

        // If hand-over is enabled, the slot is filled -> click it.
        if (handOverReady)
        {
            if (EzThrottler.Throttle("AF_HandOver", 800))
            {
                try { rq.HandOver(); }
                catch (Exception e) { Svc.Log.Verbose($"[Collect] HandOver failed: {e.Message}"); return; }
            }
            return;
        }

        // Slot not filled yet: place the collect item into the request slot. The user's flow is:
        // right-click the item in the inventory -> it fills the open Request slot. We open the
        // item's context menu against the Request addon, which fills the slot.
        if (collectItemId != 0 && EzThrottler.Throttle("AF_FillRequest", 800))
        {
            var addonId = GetAddonId("Request");
            InventoryUtil.OpenItemContextMenu(collectItemId, addonId);
        }
    }

    private static unsafe uint GetAddonId(string name)
    {
        return ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(name, out var a) && a != null
            ? (uint)a->Id : 0u;
    }

    // ---------------------------------------------------------------- escort fates
    private void HandleEscortFate(IFate fate)
    {
        // Escort: the NPC walks a route and the fate center MOVES with it. We must actively chase the
        // NPC/center (it walks away otherwise) while letting the rotation kill threats.
        StatusText = $"Escort fate: {fate.Name} {fate.Progress}%";

        // CROSS-FATE TARGET LOCK. This is the fix for "we start attacking another fate's mobs when
        // the escort walks through it." Two compounding causes:
        //   1) Our own target pick is FateId-scoped (good), but the rotation backend (Wrath/RSR/BMR)
        //      re-targets on its OWN, and a pass-through fate's nearby mobs are valid hostiles to it.
        //   2) A pass-through fate's mob can land a hit on us, pulling us into ITS fight.
        // Defence: pick a threat scoped to OUR fate only, then RE-ASSERT it every tick. If the
        // backend has yanked the target onto anything that is NOT one of our fate's enemies, we
        // forcibly take it back (or clear it) so the rotation can't keep hitting the foreign mob.
        // A Forlorn in our fate comes before the peel: it is worth more than this fate is, and it
        // leaves on its own, while the NPC we are escorting will still need peeling afterwards.
        var threat = FateTargeting.GetNearestForlorn(_targetFateId)
                     ?? FateTargeting.GetActiveDefendThreat(_targetFateId)
                     ?? FateTargeting.GetNearestFateEnemy(_targetFateId);

        var curTarget = Svc.Targets.Target as IBattleNpc;
        var curIsOurFateEnemy = curTarget != null && FateTargeting.IsFateEnemy(curTarget, _targetFateId);
        if (threat != null)
        {
            // Re-assert every tick (not just on change): the backend may have switched to a foreign
            // mob since last tick. Only keep the current target if it's already one of OUR fate's
            // enemies (don't yank a valid in-fate target mid-cast).
            if (!curIsOurFateEnemy)
                Svc.Targets.Target = threat;
            FateTargeting.StartAutoAttack(threat);
        }
        else if (curTarget != null && !curIsOurFateEnemy)
        {
            // No threat in OUR fate, but the backend has locked onto something foreign (e.g. a
            // pass-through fate's mob). Clear it so the rotation stops attacking it — we just want
            // to keep escorting.
            Svc.Targets.Target = null;
        }

        // After clicking Yes to start the fate, the escort NPC takes ~3s to (re)spawn and begin
        // walking. Wait that out before chasing so we don't path to the pre-start NPC position.
        if (_fateStartConfirmedMs != 0 && Environment.TickCount64 - _fateStartConfirmedMs < 3000)
        {
            StatusText = $"Escort starting: {fate.Name} (waiting for NPC)";
            return;
        }

        // Resolve the escort NPC. CRITICAL: cache it ONCE at the start of the fate and only ever
        // follow THAT object id. Do NOT re-pick every tick — when our escort route passes through
        // another fate, GetDefendedFriendlies could return that fate's friendly and we'd start
        // following the wrong NPC. So: if we don't have a cached id yet, grab the nearest our-fate
        // friendly and lock onto it; otherwise always resolve the cached id by object.
        IBattleNpc? escortNpc;
        if (_escortNpcId == 0)
        {
            // Prefer the game-designated MotivationNpc; only fall back to the nearest our-fate
            // friendly for true Escort-typed fates that somehow expose no MotivationNpc.
            var motivationId = FateTargeting.GetFateMotivationNpcId(_targetFateId);
            escortNpc = (motivationId != 0 ? Svc.Objects.FirstOrDefault(o => o.GameObjectId == motivationId) as IBattleNpc : null)
                        ?? FateTargeting.GetDefendedFriendlies(_targetFateId).FirstOrDefault();
            if (escortNpc != null) _escortNpcId = escortNpc.GameObjectId; // lock it in for the fate
        }
        else
        {
            // Always follow the locked NPC by id (survives FateId flicker AND passing-through fates).
            escortNpc = Svc.Objects.FirstOrDefault(o => o.GameObjectId == _escortNpcId) as IBattleNpc;
        }

        // NEVER fall back to fate.Position: the ring center TRAILS the walking NPC, so chasing it
        // runs us backward to the rear of the fate. If we genuinely can't find the NPC, hold.
        if (escortNpc == null || escortNpc.IsDead)
        {
            // Can't resolve the NPC right now — hold position (don't run to the trailing center).
            Diag("Escort", "npc", $"escort NPC unresolved/dead (cachedId={_escortNpcId}) -> holding");
            Navigator.Stop();
            return;
        }
        var followPos = escortNpc.Position;
        Diag("Escort", "follow", $"npc='{escortNpc.Name}' id={_escortNpcId} threat={(threat?.Name.ToString() ?? "<null>")} curTarget={(curTarget?.Name.ToString() ?? "<null>")} curIsOurFate={curIsOurFateEnemy} distToNpc={Vector3.Distance(Player.Object?.Position ?? followPos, followPos):F1}");

        // MOVEMENT: WE own movement for the entire escort. The old code toggled BMR movement on/off
        // every tick based on distance — that made the character chase a mob (BMR) then get yanked
        // back to the NPC (us), flapping back and forth ("walking away and back over and over").
        // Instead: forbid BMR movement ONCE (set-guarded so no chat spam) and always drive movement
        // ourselves. BMR still fights/dodges in place; the rotation kills our target.
        if (BmrMovementActive())
            IPCManager.SetBmrMovement(false); // change-guarded internally; only fires once

        var me = Player.Object;
        if (me == null) { Navigator.Stop(); return; }

        // KILL-ANY-ENEMY mode: we don't body-pull mobs back to the NPC. If OUR fate has any enemy,
        // go kill it wherever it is (no distance limit), then once they're all dead navigate back to
        // the escort NPC. `threat` was already picked + re-asserted above (FateId-scoped), so just
        // navigate into range of it and let the rotation kill it.
        if (threat != null)
        {
            _escortChasing = false; // not in NPC-follow mode while fighting
            var distToMob = Vector3.Distance(me.Position, threat.Position);
            var engage = Math.Max(2.5f, threat.HitboxRadius + 2.5f);
            if (distToMob > engage)
            {
                Navigator.FollowMoveTo(C, threat.Position, engage);
                StatusText = $"Escort: killing {threat.Name}";
            }
            else
            {
                Navigator.Stop(); // in range — stand and let the rotation finish it
                StatusText = $"Escort: fighting {threat.Name}";
            }
            return;
        }

        // NO ENEMIES LEFT: navigate back to / follow the escort NPC. Hysteresis so we don't flap:
        // start chasing past EscortNpcGlueStart, stop once within EscortNpcGlueStop. FollowMoveTo
        // re-issues as the NPC drifts.
        var distToNpc = Vector3.Distance(me.Position, followPos);
        if (_escortChasing)
        {
            if (distToNpc <= EscortNpcGlueStop) { _escortChasing = false; Navigator.Stop(); }
            else Navigator.FollowMoveTo(C, followPos, EscortNpcGlueStop);
        }
        else
        {
            if (distToNpc > EscortNpcGlueStart) { _escortChasing = true; Navigator.FollowMoveTo(C, followPos, EscortNpcGlueStop); }
            else Navigator.Stop();
        }
    }

    private const float EscortNpcGlueStart = 5f;
    private const float EscortNpcGlueStop = 2.5f;

    /// <summary>
    /// Aetheryte shortcut for the current travel leg. Returns true while the hop owns this tick
    /// (teleport cast / zoning), meaning the caller must NOT move us; false to travel normally.
    /// </summary>
    private bool TickAetheryteHop(IFate fate)
    {
        if (_hopIssuedMs != 0) return TickAetheryteHopInFlight();
        if (!C.AutoTeleportNearestAetheryte) return false;
        if (_hopEvaluatedFateId == _targetFateId) return false; // decided already for this fate

        var me = Player.Object;
        if (me == null) return false;

        // Teleport can't be cast while fighting, while the client is busy, or in the air — none of
        // those are a "no" for this fate though, so DON'T latch: re-check once we're free again.
        if (InCombat() || ECommons.GenericHelpers.IsOccupied() || Teleporter.IsBusy()) return false;
        if (Features.MountManager.IsFlying)
        {
            // Not latched (we re-check once we land), so this runs every tick: log it once per fate.
            if (_hopAirborneLoggedFateId != _targetFateId)
            {
                _hopAirborneLoggedFateId = _targetFateId;
                Diag("Movement", "hopdecision", $"'{fate.Name}': already airborne, not considering a teleport");
            }
            return false;
        }

        _hopEvaluatedFateId = _targetFateId; // from here on it's fly-there unless we hop right now

        var zone = Teleporter.AetherytesInTerritory(Svc.ClientState.TerritoryType);
        var usable = zone
            .Where(a => !_hopFailedAetherytes.Contains(a.RowId) && Teleporter.IsAttuned(a.RowId))
            .Select(a => new Logic.AetheryteHop.Aetheryte(a.RowId, a.Position))
            .ToList();
        var decision = Logic.AetheryteHop.Decide(Flat(me.Position), Flat(fate.Position), usable,
            new(C.AetheryteHopMinDistance, C.AetheryteHopMinSaving, HopArrivalDistance));
        var target = zone.FirstOrDefault(a => a.RowId == decision.AetheryteId);
        // Once per fate, so this is cheap, and it's the only way to tell WHY we flew.
        Diag("Movement", "hopdecision", $"'{fate.Name}': {decision.Verdict} (fate {decision.Distance:F0}y away, "
            + $"best={(decision.AetheryteId != 0 ? target.Name : "none")} saves {decision.Saved:F0}y, "
            + $"aetherytes: {zone.Count} in zone, {usable.Count} usable)");
        if (decision.Verdict != Logic.AetheryteHop.Verdict.Hop) return false;

        Navigator.Stop(); // moving cancels the cast
        if (!Teleporter.TeleportToAetheryteId(target.RowId)) return false;

        _hopAetheryteId = target.RowId;
        _hopDestination = target.Position;
        _hopIssuedMs = Environment.TickCount64;
        _hopLastBusyMs = 0;
        Diag("Movement", "hop", $"teleporting to {target.Name} for '{fate.Name}' (saves ~{decision.Saved:F0}y of {decision.Distance:F0}y)");
        StatusText = $"Teleporting to {target.Name}";
        return true;
    }

    /// <summary>World X/Z of a position, the plane aetheryte positions live in.</summary>
    private static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    /// <summary>Wait out an issued hop. Returns true while it's still in flight.</summary>
    private bool TickAetheryteHopInFlight()
    {
        var now = Environment.TickCount64;
        var me = Player.Object;

        if (now - _hopIssuedMs > HopTimeoutMs)
        {
            Diag("Movement", "hop", $"teleport to aetheryte {_hopAetheryteId} timed out; traveling normally");
            _hopIssuedMs = 0;
            return false;
        }

        // Casting or loading: hold still.
        if (Teleporter.IsBusy() || me == null)
        {
            _hopLastBusyMs = now;
            StatusText = "Teleporting to a closer aetheryte...";
            return true;
        }

        if (Vector2.Distance(Flat(me.Position), _hopDestination) <= HopArrivalDistance)
        {
            _hopIssuedMs = 0; // landed — resume normal travel from here
            return false;
        }

        // Neither busy nor there yet. The cast takes a moment to register, so give it a grace
        // window before deciding nothing happened.
        if (now - Math.Max(_hopIssuedMs, _hopLastBusyMs) < HopSettleMs)
        {
            StatusText = "Teleporting to a closer aetheryte...";
            return true;
        }

        // No cast, no zoning, still here. If the cast NEVER started, this aetheryte is out of reach
        // for us (not attuned, or not enough gil) -> drop it for the session so we stop paying the
        // grace window on it every fate. If it started and then died (aggro, damage), keep the
        // aetheryte and just fly this once.
        var neverStarted = _hopLastBusyMs == 0 && !InCombat();
        Diag("Movement", "hop", $"teleport to aetheryte {_hopAetheryteId} didn't land; traveling normally (blacklist={neverStarted})");
        if (neverStarted) _hopFailedAetherytes.Add(_hopAetheryteId);
        _hopIssuedMs = 0;
        return false;
    }

    /// <summary>
    /// Clear per-fate carry-over state. CRITICAL: escort/collect state (especially the cached escort
    /// NPC id) must be wiped whenever we move to a NEW fate, not only via OnFateFinished — an escort
    /// fate can end WITHOUT OnFateFinished (expired, abandoned, completed by other players). If the
    /// stale _escortNpcId leaked into the next fate, hasEscortNpc stayed true and we'd run the escort
    /// handler forever on a non-escort fate, holding while "looking for" a dead NPC.
    /// </summary>
    private void ResetPerFateState()
    {
        _preparingSinceMs = 0;
        _npcStartAttempts = 0;
        _fateStartConfirmedMs = 0;
        _escortNpcId = 0;
        _escortChasing = false;
        _engagedTargetId = 0;
        _massPullTargetId = 0;
        _strayTargetId = 0;
        _strayDeadlineMs = 0;
        _strayWrittenOff.Clear();
        ClearKite();
        _kiteGaveUp.Clear();
        _collectInteractObjId = 0;
        _collectInteractCooldownMs = 0;
        _collectShedPoint = null;
        _collectShedDeadlineMs = 0;
        _collectShedBlockedUntilMs = 0;
        _collectFinishTargetId = 0;
        _collectFinishDeadlineMs = 0;
        _yieldUntilMs = 0;
        _fatePosSampled = false;
        _ringMovedFollow = false;
        _fateDropoff = null;
        _climbingOut = false;
        _dropoffSafetyChecked = false;
        _travelProgress.Reset();
        _dropoffRerolls = 0;
        _dismountSinceMs = 0;
        _landOnDropoff = false;
        _fateStuckLastSampleMs = 0;
        _groundStuckLastMs = 0;
        _groundJumpPhase = 0;
        _groundNudgeTarget = null;
        _inFateMountMs = 0;
        _hopEvaluatedFateId = 0;
        _hopIssuedMs = 0;
        _hopLastBusyMs = 0;
    }

    private void OnFateFinished() => LeaveFate(completed: true);

    /// <summary>
    /// Walk away from a fate that has nothing left to give us — a collect fate in its last phase
    /// with an empty bag. Same cleanup as a completion, but it is not one: it must not count towards
    /// the session's fates, the per-zone quotas, or the chocobo XP check (which reads "no XP this
    /// fate" as a rank-cap stall).
    /// </summary>
    private void AbandonFate(string reason)
    {
        if (C.VerboseLogging) Svc.Log.Information($"[Diag/Fate] abandoning fate {_targetFateId}: {reason}");
        LeaveFate(completed: false);
    }

    private void LeaveFate(bool completed)
    {
        if (completed)
        {
            Stats.OnFateCompleted();
            Features.ChocoboManager.CheckXpGainAfterFate(); // detect rank-cap stall (no XP gained this fate)
        }
        Navigator.Stop();
        _yieldUntilMs = 0; // clear the smart-mix yield latch
        _engagedTargetId = 0; // drop the sticky combat target so the next fate re-selects fresh
        _massPullTargetId = 0; // drop the sticky body-pull target too
        ClearKite();
        TextAdvanceIPC.Disable(); // release collect turn-in control (no-op if we never took it)

        if (completed)
        {
            // In Shared FATEs mode, re-open the Shared FATE window to repopulate per-zone progress,
            // but only every 5 completed fates (opening the window is disruptive, so we don't do it
            // every fate). EnsureData will re-capture and spam-close it on the following ticks.
            if (C.Mode == FarmingMode.SharedFates)
            {
                _fatesSinceFateDataRefresh++;
                if (_fatesSinceFateDataRefresh >= 5)
                {
                    _fatesSinceFateDataRefresh = 0;
                    Features.SharedFateTracker.RefreshData(force: true);
                }
            }

            // Per-zone counters (manual mode quota).
            var terr = Svc.ClientState.TerritoryType;
            _zoneFatesDone.TryGetValue(terr, out var done);
            _zoneFatesDone[terr] = done + 1;

            if (C.Mode == FarmingMode.Manual)
            {
                var entry = C.ManualZones.FirstOrDefault(z => z.TerritoryId == terr);
                if (entry != null) entry.FatesDone++;
            }
        }

        _targetFateId = 0;
        _startedFateId = 0;
        _fateNpcInteractedMs = 0;
        _fateStartConfirmedMs = 0;
        _postFateGraceUntilMs = Environment.TickCount64 + PostFateGraceMs; // wait for a chained spawn
        ResetPerFateState();
        _dismountedForNpcMs = 0;
        // Escort may have forbidden BMR movement — hand it back so the next fate behaves normally.
        if (BmrMovementActive()) IPCManager.SetBmrMovement(true);

        // If we're still in combat (we pulled stray non-fate enemies — possibly hitting our chocobo
        // rather than us), clear them before moving on so we don't get stuck.
        if (InCombat())
        {
            State = FarmState.ClearingAggro;
            return;
        }

        State = FarmState.SelectingFate;
    }

    /// <summary>True if the player is flagged in combat.</summary>
    private static bool InCombat()
    {
        var me = Player.Object;
        return me != null
            && (me.StatusFlags & Dalamud.Game.ClientState.Objects.Enums.StatusFlags.InCombat) != 0;
    }

    /// <summary>Kill any stray hostiles until we're out of combat, then resume fate selection.</summary>
    /// <summary>
    /// While grounded + navigating, if we barely move for >2s we're blocked by geometry. Back out
    /// ~5y (opposite our facing), then force the path to regenerate. Returns true if it consumed the
    /// tick (caller must return so nothing else issues movement this frame).
    /// </summary>
    private bool TickGroundedStuck()
    {
        var me = Player.Object;
        if (me == null) return false;

        // Drive an in-progress back-out nudge to completion before anything else moves us.
        if (_groundNudgeTarget is { } back)
        {
            if (Vector3.Distance(me.Position, back) <= 2f || Environment.TickCount64 >= _groundNudgeUntilMs)
            {
                _groundNudgeTarget = null;
                Navigator.Stop();                 // clears last dest -> next MoveTo regenerates the path
                _groundStuckLastMs = 0;           // restart the stuck sampler
                _groundJumpPhase = 0;
                return true;
            }
            Autofate.IPC.NavmeshIPC.MoveTo(new List<Vector3> { back }, _groundNudgeFly);
            StatusText = "Unblocking (backing out)";
            return true;
        }

        // Watch while navigating on foot OR flying (mounted-ground doesn't get wedged the same way).
        var flying = Features.MountManager.IsFlying;
        if (!Navigator.IsNavigating || Player.IsJumping
            || (Features.MountManager.IsMounted && !flying))
        {
            _groundStuckLastMs = 0;
            return false;
        }

        // FLYING + STUCK: vnav can think we can move forward into a wall (e.g. inside a cave) and
        // keeps regenerating the path into it. Skip the jump phase (useless airborne) and go STRAIGHT
        // to the back-out + renav: reverse ~5y along our facing (in 3D, fly) then regenerate.
        if (flying)
        {
            var now2 = Environment.TickCount64;
            if (_groundStuckLastMs == 0) { _groundStuckLastMs = now2; _groundStuckLastPos = me.Position; return false; }
            if (now2 - _groundStuckLastMs < GroundStuckWindowMs) return false;
            var moved2 = Vector3.Distance(me.Position, _groundStuckLastPos);
            _groundStuckLastMs = now2;
            _groundStuckLastPos = me.Position;
            if (moved2 >= GroundStuckMinMove) return false; // moving fine

            var rotF = me.Rotation;
            var behindF = new Vector3(-MathF.Sin(rotF), 0f, -MathF.Cos(rotF)) * 5f;
            _groundNudgeTarget = me.Position + behindF;
            _groundNudgeFly = true;
            _groundNudgeUntilMs = now2 + 2000;
            Navigator.Stop();
            Autofate.IPC.NavmeshIPC.MoveTo(new List<Vector3> { _groundNudgeTarget.Value }, true);
            Diag("Movement", "flystuck", $"flying & moved {moved2:F1}y -> backing out 5y then repath");
            return true;
        }

        var now = Environment.TickCount64;
        if (_groundStuckLastMs == 0) { _groundStuckLastMs = now; _groundStuckLastPos = me.Position; return false; }
        if (now - _groundStuckLastMs < GroundStuckWindowMs) return false;

        var moved = Vector3.Distance(me.Position, _groundStuckLastPos);
        _groundStuckLastMs = now;
        _groundStuckLastPos = me.Position;
        if (moved >= GroundStuckMinMove) { _groundJumpPhase = 0; return false; } // moving fine

        // JUMP PHASE FIRST: jump, wait for grounded, jump again, wait the SAME window. Only if we
        // STILL haven't moved after that do we back out + renav. A single hop often clears a small
        // lip/step without the heavier back-out maneuver.
        unsafe void Jump() => FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance()
            ->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 2 /* Jump */);

        if (_groundJumpPhase == 0)
        {
            _groundJumpStartPos = me.Position;
            Navigator.Stop();
            Jump();
            _groundJumpPhase = 1; // wait for grounded after the first jump
            Diag("Movement", "groundstuck", $"moved {moved:F1}y -> jump 1");
            return true;
        }
        if (_groundJumpPhase == 1)
        {
            if (Player.IsJumping) return true;     // still airborne from jump 1
            Jump();
            _groundJumpPhase = 2;
            _groundJumpWaitUntilMs = now + GroundStuckWindowMs; // wait the same window after jump 2
            Diag("Movement", "groundstuck", "grounded -> jump 2, waiting window");
            return true;
        }
        // phase 2: waiting the window after the second jump.
        if (Player.IsJumping || now < _groundJumpWaitUntilMs) return true;
        _groundJumpPhase = 0;
        if (Vector3.Distance(me.Position, _groundJumpStartPos) >= GroundStuckMinMove)
        {
            // The jumps freed us. Resume normal navigation, restart the sampler.
            _groundStuckLastMs = 0;
            Diag("Movement", "groundstuck", "jumps cleared the block -> resuming nav");
            return true;
        }

        // Still wedged after the jumps -> back straight out ~5y (FFXIV yaw: forward = (sin,0,cos)).
        var rot = me.Rotation;
        var behind = new Vector3(-MathF.Sin(rot), 0f, -MathF.Cos(rot)) * 5f;
        _groundNudgeTarget = me.Position + behind;
        _groundNudgeFly = false;
        _groundNudgeUntilMs = now + 2000;
        Navigator.Stop();
        Autofate.IPC.NavmeshIPC.MoveTo(new List<Vector3> { _groundNudgeTarget.Value }, false);
        Diag("Movement", "groundstuck", "jumps failed -> backing out 5y then repath");
        return true;
    }

    private void TickClearingAggro()
    {
        // ONLY fight enemies ACTUALLY attacking us (or our chocobo). Never target a non-fate enemy
        // that isn't aggro'd on us - no GetNearestHostile fallback. If nothing is on us, we're done,
        // even if the in-combat flag is still lingering.
        //
        // Checked BEFORE we touch the mount. The mount is what gets us to the next fate, so we only
        // give it up once we know there is something here to fight: dismounting first meant a mob
        // that tagged us in passing and then leashed still cost us the mount and the trip.
        var attackers = FateTargeting.GetEnemiesAttackingMe();
        var hostile = attackers.Count > 0 ? attackers[0] : null;

        Diag("Combat", "clearaggro", $"attackers={attackers.Count} hostile={(hostile?.Name.ToString() ?? "<null>")} inCombat={InCombat()} mounted={Features.MountManager.IsMounted} flying={Features.MountManager.IsFlying}");

        if (hostile == null)
        {
            Navigator.Stop();
            SetCombatBackend(false);
            State = FarmState.SelectingFate;
            return;
        }

        // GROUNDED GUARD: land + dismount before targeting/engaging stray aggro.
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            Navigator.Stop();
            Features.MountManager.Dismount();
            StatusText = "Landing/dismounting before clearing aggro";
            return;
        }

        var attacker = hostile;
        if (!(Svc.Targets.Target is IBattleNpc cur && FateTargeting.IsAttackableEnemy(cur)
              && (cur.GameObjectId == attacker.GameObjectId)))
            Svc.Targets.Target = attacker;

        StatusText = $"Clearing stray aggro: {attacker.Name}";
        // Make the rotation backend fight it. BMR is pinned to this target for as long as we are in
        // this state (top of Tick), so the rotation does not AOE or auto-target the mobs next to it.
        SetCombatBackend(true);

        // Walk into range if the backend isn't moving us.
        var me = Player.Object;
        if (me != null && !BmrMovementActive() && !ECommons.GenericHelpers.IsOccupied())
        {
            var dist = Vector3.Distance(me.Position, attacker.Position);
            var engageRange = Math.Max(2.5f, attacker.HitboxRadius + 2.5f);
            if (dist > engageRange)
                Navigator.MoveTo(C, attacker.Position, engageRange, allowMount: false);
            else
                Navigator.Stop();
        }
    }

    // ---------------------------------------------------------------- follow leader
    private void TickFollowLeader()
    {
        if (Svc.Party.Length == 0)
        {
            StatusText = "Follow: not in a party";
            if (EzThrottler.Throttle("AF_NoParty", 8000))
                Svc.Log.Information($"[Follow] Not in a party (Party.Length=0). LeaderIdx={Svc.Party.PartyLeaderIndex}.");
            return;
        }

        var leader = GetPartyLeaderObject();
        var me = Player.Object;

        // If WE are the leader (or the leader object is us), there's nobody to follow — just hold
        // and let combat/sync handle whatever fate we're standing in.
        if (leader == null || me == null || leader.GameObjectId == me.GameObjectId)
        {
            StatusText = leader == null ? "Follow: leader object not loaded (out of range?)" : "Follow: you are the leader";
            if (EzThrottler.Throttle("AF_FollowSelf", 5000))
                Svc.Log.Information($"[Follow] No distinct leader. Party.Length={Svc.Party.Length}, LeaderIdx={Svc.Party.PartyLeaderIndex}, leaderObj={(leader == null ? "null" : leader.Name.ToString())}.");
            var f0 = FateSelector.GetCurrentFate();
            if (f0 != null && C.AutoLevelSync) SyncToFate();
            return;
        }

        // HAND OFF TO THE FATE MACHINE: if the leader has dropped us inside a running fate (and that
        // fate type is enabled), stop following and run it via the normal InFate flow — which lands,
        // dismounts, starts the fate via NPC if needed, syncs, and fights. When the fate ends,
        // OnFateFinished sets State=SelectingFate, and the top-of-tick follow redirect resumes us
        // here to follow the leader again.
        var fate = FateSelector.GetCurrentFate();
        if (fate != null)
        {
            Navigator.Stop();
            _targetFateId = fate.FateId;
            _startedFateId = 0;
            _fateNpcInteractedMs = 0;
            ResetPerFateState(); // clear escort/collect carry-over from any previous fate
            // Route through TravelingToFate (NOT straight to InFate) so we walk to the fate CENTER —
            // same dropoff as normal farming. GetCurrentFate triggers at the ring EDGE, and InFate
            // only re-centers when outside the ring, so jumping straight to InFate left us stranded
            // at the edge. TravelingToFate drives to arrivalRange (4y) of center.
            State = FarmState.TravelingToFate;
            StatusText = $"Follow: entering fate {fate.Name}";
            return;
        }

        var distToLeader = Vector3.Distance(me.Position, leader.Position);
        StatusText = $"Following {leader.Name} ({distToLeader:0}y)";

        // ALWAYS mount up while following (not in a fate) so we keep pace with the leader's mount.
        // Mount but DON'T return — fall through to FollowMoveTo so vnav starts pathing (and takes
        // off into flight) the same frame instead of waiting a tick.
        if (!MountManager.IsMounted && MountManager.CanMountHere
            && distToLeader > C.FollowDistance + 2f
            && !ECommons.GenericHelpers.IsOccupied())
        {
            MountManager.Mount(C);
        }

        // ALWAYS use vnavmesh to chase the leader's LIVE position. (We deliberately do NOT use
        // BMR's /bmrai follow here — that only works when BMR AI is enabled, which it isn't in
        // follow mode, so it silently did nothing.)
        Navigator.FollowMoveTo(C, leader.Position, C.FollowDistance);
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? GetPartyLeaderObject()
    {
        var idx = Svc.Party.PartyLeaderIndex;
        if (idx >= Svc.Party.Length) return null;
        var member = Svc.Party[(int)idx];
        if (member == null) return null;

        // member.GameObject is null when the leader isn't in the local object table yet. Fall back
        // to resolving by the party member's EntityId/ObjectId against the live object table.
        if (member.GameObject != null) return member.GameObject;

        var wantEntity = member.EntityId; // entity id of the party member
        foreach (var obj in Svc.Objects)
        {
            if (obj.EntityId == wantEntity) return obj;
        }
        return null;
    }

    // ---------------------------------------------------------------- maintenance gating
    /// <summary>If any maintenance task is needed, switch into the appropriate state. Returns true if entered.</summary>
    private bool TryEnterMaintenance()
    {
        // Chocobo leveling has priority when the chocobo needs stabling/feeding.
        if (C.ChocoboLevelingEnabled && ChocoboStableRoutine.NeedsAttention(C))
        {
            ChocoboStableRoutine.ResetSteps(); // fresh stable step machine each time we enter
            // The stable is a CUSTOM menu flow we drive ourselves (SelectString "Tend to my Chocobo"
            // + Fetch Yes/No). TextAdvance, enabled session-wide, would auto-handle those addons and
            // race/hijack our manual menu logic — that's what broke the fetch step. Release TA for
            // the whole stable cycle; we re-enable it when the routine finishes (TickChocoboLeveling).
            TextAdvanceIPC.Disable();
            State = FarmState.ChocoboLeveling;
            return true;
        }

        // Repair.
        if (C.AutoRepair && RepairManager.NeedsRepair(C))
        {
            State = FarmState.Maintenance;
            return true;
        }

        // Gemstone shopping. Don't re-enter right after a session unless we've farmed more gems
        // (prevents the open->close->reinteract loop caused by continuous-buy always "wanting more").
        if (GemstoneShopper.ShouldShop(C)
            && (_gemCountAfterLastShop < 0 || Features.InventoryUtil.GetGemstoneCount() > _gemCountAfterLastShop))
        {
            // Remember where we came from so we can return straight to it when shopping is done.
            // Always return to SelectingZone after shopping — we'll be in the vendor's zone and need
            // to re-pick + travel back to a farming zone for the current mode.
            EnterShopping();
            return true;
        }

        return false;
    }

    private void TickMaintenance()
    {
        StatusText = "Maintenance: repair";
        if (!RepairManager.NeedsRepair(C))
        {
            State = FarmState.SelectingZone;
            return;
        }

        // Self-repair flow (no crafter gearset needed — dark matter repairs any gear on any class).
        if (C.RepairMode == RepairMode.SelfRepair)
        {
            if (!RepairManager.CanSelfRepair())
            {
                Stop(StopReason.OutOfDarkMatter);
                return;
            }
            // RunSelfRepair returns true once everything is repaired AND the window is closed.
            if (RepairManager.RunSelfRepair(C))
                State = FarmState.SelectingZone;
            return;
        }

        // TODO(WIP): Mender NPC repair isn't automated (greyed in UI). Needs vendor routing +
        // repair-all-from-NPC flow. For now, warn and continue.
        if (EzThrottler.Throttle("AF_NpcRepair", 30000))
            Svc.Chat.PrintError("[Autofate] NPC repair not yet automated; switch to self-repair.");
        State = FarmState.SelectingZone;
    }

    private void TickChocoboLeveling()
    {
        StatusText = "Chocobo leveling";
        // The routine only returns true once the chocobo has actually been FETCHED back out (the
        // fetch loop retries close->reinteract->Tend->Fetch->Yes until it succeeds). No possession
        // check is needed here — the stable routine doesn't finish until the fetch is done.
        if (ChocoboStableRoutine.Tick(C))
        {
            // Stable cycle done — hand dialogue back to TextAdvance for normal fate/turn-in flow.
            TextAdvanceIPC.Enable();
            if (C.StopAtChocoboMaxed && ChocoboManager.ReachedTargetLevel(C))
                Stop(StopReason.ChocoboMaxed);
            else
                State = FarmState.SelectingZone;
        }
    }

    /// <summary>Enter the gemstone-shopping state for a fresh visit (clears the per-visit latch).</summary>
    private void EnterShopping()
    {
        _stateBeforeShop = FarmState.SelectingZone;
        _shoppingDone = false; // fresh visit: allow interacting/buying again
        State = FarmState.GemstoneShopping;
    }

    /// <summary>
    /// Conclude the current shopping visit: latch DONE, record the gem count so we don't re-enter
    /// until we've farmed more, close the window if it's open, and only leave the state once the
    /// window is actually gone. While latched, the rest of TickGemstoneShopping refuses to
    /// re-interact the vendor — this is what stops the open->buy->close->reopen loop.
    /// </summary>
    private void FinishShopping()
    {
        _shoppingDone = true;
        _gemCountAfterLastShop = Features.InventoryUtil.GetGemstoneCount();
        if (GemstoneShopper.ShopOpen())
        {
            if (EzThrottler.Throttle("AF_CloseShop", 300))
                GemstoneShopper.CloseShop();
            return; // wait for it to actually close before leaving the state
        }
        State = _stateBeforeShop;
    }

    private unsafe void TickGemstoneShopping()
    {
        StatusText = "Gemstone shopping";

        // PER-VISIT DONE LATCH: once a visit is judged complete we ONLY close + leave; we never fall
        // through to re-target/re-interact the vendor (ShouldShop can still read true for continuous
        // entries, which is exactly what used to reopen the window in a loop).
        if (_shoppingDone)
        {
            FinishShopping();
            return;
        }

        // TOP-OF-TICK COMPLETION GUARD (works whether the shop is open or not): if every enabled
        // capped buy entry has reached its target item count AND there are no continuous entries,
        // we're DONE. This catches completion even before the shop opens.
        var hasContinuous = C.GemstoneBuyList.Any(e => e.Enabled && e.TargetQuantity == 0);
        if (!hasContinuous && GemstoneShopper.AllTargetsMet(C))
        {
            FinishShopping();
            return;
        }

        // If the shop addon is open, buy until done. FORCIBLE COMPLETION: the instant there's
        // nothing left we can AND want to buy (every entry capped, unaffordable, or — for continuous
        // entries — drained back to the threshold), latch DONE and leave. This check only runs while
        // the shop is OPEN (item costs are only readable then) so we don't bail mid-travel.
        if (GemstoneShopper.ShopOpen())
        {
            if (GemstoneShopper.BuyingComplete(C))
            {
                FinishShopping();
                return;
            }
            GemstoneShopper.PurchaseTick(C);
            return;
        }

        // Interacting with the vendor often pops Talk dialogue (greeting) BEFORE the shop addon
        // opens. TextAdvance (session-wide) advances it for us; only spam-click it ourselves as a
        // FALLBACK when TextAdvance isn't installed. Gate on IsAddonReady to avoid NREs.
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("Talk", out var gtalk)
            && ECommons.GenericHelpers.IsAddonReady(gtalk))
        {
            if (!TextAdvanceIPC.ControlActive && EzThrottler.Throttle("AF_GemTalk", 200))
            {
                try { new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Talk((nint)gtalk).Click(); }
                catch (Exception e) { Svc.Log.Verbose($"[Gemstone] Talk click failed: {e.Message}"); }
            }
            return;
        }

        // No captured vendor location -> we can't auto-travel. Tell the user how to set it once.
        if (!C.VendorPositionSet)
        {
            if (EzThrottler.Throttle("AF_GemNoVendor", 30000))
                Svc.Chat.Print("[Autofate] Open the gemstone vendor and click 'Add' on an item in the Gemstone tab to capture its location for auto-travel.");
            if (!GemstoneShopper.ShouldShop(C)) State = FarmState.SelectingZone;
            return;
        }

        // STEP 1: teleport to the vendor's zone (nearest aetheryte) if we're not there yet.
        if (Svc.ClientState.TerritoryType != C.VendorTerritory)
        {
            StatusText = $"Traveling to gemstone vendor in {Data.Zones.GetTerritoryName(C.VendorTerritory)}";
            Teleporter.TravelToTerritory(C, C.VendorTerritory);
            return;
        }

        // STEP 2: in the vendor's zone — dismount, then navigate to the vendor and interact.
        var me = Player.Object;
        if (me == null) return;

        // Find the live vendor NPC (by captured DataId) so we interact with the exact object;
        // fall back to the captured position if the NPC isn't loaded yet.
        var vendor = FindGemstoneVendor();
        var dest = vendor?.Position ?? C.VendorPosition;
        var dist = Vector3.Distance(me.Position, dest);

        if (dist > 4f)
        {
            StatusText = $"Navigating to {(string.IsNullOrEmpty(C.VendorName) ? "gemstone vendor" : C.VendorName)}";
            Navigator.MoveTo(C, dest, 3f);
            return;
        }

        // In range — dismount before interacting.
        Navigator.Stop();
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            Features.MountManager.Dismount();
            return;
        }

        if (vendor == null)
        {
            // We're at the captured spot but the NPC isn't found (moved patch / wrong capture).
            if (EzThrottler.Throttle("AF_GemVendorMissing", 15000))
                Svc.Chat.PrintError("[Autofate] Reached the vendor spot but couldn't find the vendor NPC. Re-capture it in the Gemstone tab.");
            return;
        }

        if (Player.IsAnimationLocked || !Player.Interactable) return;
        if (Svc.Targets.Target?.GameObjectId != vendor.GameObjectId)
            Svc.Targets.Target = vendor;
        if (EzThrottler.Throttle("AF_GemInteract", 1500))
        {
            StatusText = $"Opening shop: {vendor.Name}";
            FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()
                ->InteractWithObject(((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)vendor.Address), false);
        }
    }

    /// <summary>Find the captured gemstone vendor NPC nearby (by BaseId/DataId), or null.</summary>
    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindGemstoneVendor()
    {
        if (C.VendorDataId == 0) return null;
        var me = Player.Object;
        if (me == null) return null;
        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestSq = float.MaxValue;
        foreach (var o in Svc.Objects)
        {
            if (o.BaseId != C.VendorDataId) continue;
            var d = Vector3.DistanceSquared(me.Position, o.Position);
            if (d < bestSq) { bestSq = d; best = o; }
        }
        return best;
    }
}

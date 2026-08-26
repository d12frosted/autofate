using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;

namespace Autofate.UI;

/// <summary>
/// The small runtime overlay shown while farming. Deliberately read-only apart from three
/// controls (pause/resume, stop, open settings): everything it shows already lives on the
/// controller, so this window only formats it.
/// </summary>
public sealed class OverlayWindow : Window
{
    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoFocusOnAppearing;

    private static Configuration C => Plugin.C;
    private static Core.FarmingController Controller => Plugin.Instance!.Controller;

    // Inventory / goal values are sampled on a timer rather than every frame: they change slowly
    // and some of them walk the whole collectable list.
    private const long SampleIntervalMs = 500;
    private long _lastSampleMs;
    private int _heldGems;
    private Goal? _goal;

    // Position is written back to config only once the drag ends, not on every frame of it.
    private bool _positionDirty;

    // Left edge of the content area in window-local coords, so rows can right-align against it.
    private float _contentX;

    // One-shot: apply the configured position even though ImGui already remembers one.
    private bool _forcePosition;

    public OverlayWindow() : base("Autofate###AutofateOverlay", BaseFlags)
    {
        RespectCloseHotkey = false; // Escape shouldn't dismiss it; it would just reopen next frame
        DisableWindowSounds = true;
    }

    /// <summary>A run goal with a denominator worth drawing a bar for.</summary>
    private readonly record struct Goal(string Label, int Current, int Max)
    {
        public float Fraction => Max <= 0 ? 0f : Math.Clamp((float)Current / Max, 0f, 1f);
    }

    public override void PreDraw()
    {
        Flags = C.OverlayLocked ? BaseFlags | ImGuiWindowFlags.NoMove : BaseFlags;
        if (C.OverlayPosition != Vector2.Zero)
        {
            var cond = _forcePosition || C.OverlayLocked ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
            ImGui.SetNextWindowPos(C.OverlayPosition, cond);
        }
        _forcePosition = false;
        ImGui.SetNextWindowBgAlpha(Math.Clamp(C.OverlayAlpha, 0.2f, 1f));
    }

    /// <summary>Drop the overlay back to a known spot, for when it ends up off-screen.</summary>
    public void ResetPosition()
    {
        C.OverlayPosition = ImGuiHelpers.MainViewport.WorkPos + new Vector2(40f, 40f) * ImGuiHelpers.GlobalScale;
        _forcePosition = true;
        C.Save();
    }

    public override void Draw()
    {
        Sample();
        TrackPosition();

        // Pin the width: with AlwaysAutoResize the window would otherwise breathe in and out with
        // the length of the status line, and nothing could be right-aligned against it.
        var width = (C.OverlayCompact ? 260f : 330f) * ImGuiHelpers.GlobalScale;
        _contentX = ImGui.GetCursorPosX();
        ImGui.Dummy(new Vector2(width, 0));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y);

        if (C.OverlayCompact) DrawCompact(width);
        else DrawFull(width);
    }

    // ---------------------------------------------------------------- layouts
    private void DrawCompact(float width)
    {
        // The buttons make this row frame-height, so text on it has to be baseline-shifted or it
        // rides along the top edge instead of sitting level with them.
        ImGui.AlignTextToFramePadding();
        StatusDot(ImGui.GetFrameHeight());
        ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X);
        DrawStatusText(_contentX + width - ControlsWidth() - ImGui.GetCursorPosX() - ImGui.GetStyle().ItemSpacing.X);
        SameLineControls(width);
        DrawControls();
    }

    private void DrawFull(float width)
    {
        // Row 1: state dot, name, runtime, controls. The buttons make this row frame-height, so
        // the text has to be baseline-shifted or it rides along the top edge instead of sitting
        // level with them.
        ImGui.AlignTextToFramePadding();
        StatusDot(ImGui.GetFrameHeight());
        ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X);
        ImGui.TextUnformatted("Autofate");
        ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X * 2);
        var runtime = Controller.Stats.Runtime;
        // Not "hh": that wraps at a day, and this thing is meant to run overnight.
        ImGuiEx.Text(ImGuiColors.DalamudGrey,
            $"{(int)runtime.TotalHours:00}:{runtime.Minutes:00}:{runtime.Seconds:00}");
        ImGuiEx.Tooltip("Time spent farming this session (paused time doesn't count).");
        SameLineControls(width);
        DrawControls();

        // Row 2: what it's actually doing.
        DrawStatusText(width);

        // Row 3: session numbers.
        DrawStats();

        // Row 4: progress towards whatever this run's goal is.
        if (_goal is { } goal)
        {
            var barColor = Controller.Paused ? ImGuiColors.DalamudGrey : ImGuiColors.ParsedBlue;
            using (ImRaii.PushColor(ImGuiCol.PlotHistogram, barColor))
                ImGui.ProgressBar(goal.Fraction, new Vector2(width, 3f * ImGuiHelpers.GlobalScale), string.Empty);
            ImGuiEx.Tooltip($"{goal.Label}: {goal.Current} / {goal.Max}");
        }
    }

    private void DrawStats()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X * 2;
        var s = Controller.Stats;

        Chip("FATEs", s.FatesCompleted.ToString(), null,
            $"FATEs completed this session ({s.FatesAttempted} attempted).");

        ImGui.SameLine(0, spacing);
        var cap = Data.GameItems.BicolorGemstoneCap;
        var nearCap = _heldGems >= cap * 0.95f;
        Chip("Gems", $"{_heldGems}/{cap}", nearCap ? ImGuiColors.DalamudOrange : null,
            nearCap
                ? $"Bicolor gemstones held. You're at the cap, so FATE rewards are being wasted ({s.GemstonesGained} earned this session)."
                : $"Bicolor gemstones held ({s.GemstonesGained} earned this session).");

        ImGui.SameLine(0, spacing);
        var gained = s.CurrentLevel - s.StartLevel;
        Chip("Lv", gained > 0 ? $"{s.CurrentLevel} +{gained}" : s.CurrentLevel.ToString(), null,
            $"Started at level {s.StartLevel}.");

        if (s.Deaths <= 0) return;
        ImGui.SameLine(0, spacing);
        Chip("Deaths", s.Deaths.ToString(), ImGuiColors.DalamudRed,
            C.Mode == FarmingMode.Leveling ? "Leveling mode stops the run at 3 deaths." : "Deaths this session.");
    }

    private void DrawStatusText(float maxWidth)
    {
        var color = Controller.Paused ? ImGuiColors.DalamudOrange : ImGuiColors.DalamudWhite;
        var text = Controller.StatusText;
        var shown = text;
        if (ImGui.CalcTextSize(text).X > maxWidth)
        {
            while (shown.Length > 1 && ImGui.CalcTextSize($"{shown}...").X > maxWidth)
                shown = shown[..^1];
            shown += "...";
        }
        ImGuiEx.Text(color, shown);
        ImGuiEx.Tooltip($"{text}\n\nState: {Controller.State}\nClick to open the Status tab.");
        if (ImGui.IsItemClicked())
        {
            Controller.ForceStatusTab = true;
            Plugin.Instance!.ShowMainWindow();
        }
    }

    // ---------------------------------------------------------------- controls
    private static float ControlsWidth()
        => ImGui.GetFrameHeight() * 3 + ImGui.GetStyle().ItemSpacing.X * 2;

    private void SameLineControls(float width)
        => ImGui.SameLine(_contentX + width - ControlsWidth());

    private void DrawControls()
    {
        var paused = Controller.Paused;

        if (IconButton(paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause, "afo_pause",
                paused ? ImGuiColors.DalamudOrange : null))
            Controller.TogglePause();
        ImGuiEx.Tooltip(paused
            ? "Resume. A FATE held over a long pause has probably expired, so we'll pick a new one."
            : "Pause: stops navigation and shuts the combat backends down, keeping the session.\n"
              + "It can't interrupt dialogue already in flight, and with the AI off nothing is\n"
              + "dodging for you, so pausing mid-pull is dangerous.");

        ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X);
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.55f, 0.19f, 0.19f, 1f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.68f, 0.23f, 0.23f, 1f)))
        {
            if (IconButton(FontAwesomeIcon.Stop, "afo_stop", ImGuiColors.DalamudRed))
                Controller.Stop();
        }
        ImGuiEx.Tooltip("Stop the run.");

        ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X);
        if (IconButton(FontAwesomeIcon.Cog, "afo_cog", null))
            Plugin.Instance!.ShowMainWindow();
        ImGuiEx.Tooltip("Open the settings window. This doesn't stop the run.");
    }

    /// <summary>
    /// A square icon button whose glyph is actually in the middle of it. ImGui centres a button's
    /// label inside the rect MINUS FramePadding, so on a button this narrow the icon is clipped
    /// against the left padding instead of centred: draw the glyph ourselves over the whole rect.
    /// </summary>
    private static bool IconButton(FontAwesomeIcon icon, string id, Vector4? color)
    {
        var side = ImGui.GetFrameHeight();
        var origin = ImGui.GetCursorScreenPos();
        var pressed = ImGui.Button($"##{id}", new Vector2(side, side));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            // Whole pixels, or the glyph renders soft.
            var at = new Vector2(
                MathF.Round(origin.X + (side - glyphSize.X) * 0.5f),
                MathF.Round(origin.Y + (side - glyphSize.Y) * 0.5f));
            var tint = color is { } c ? ImGui.ColorConvertFloat4ToU32(c) : ImGui.GetColorU32(ImGuiCol.Text);
            ImGui.GetWindowDrawList().AddText(at, tint, glyph);
        }

        return pressed;
    }

    // ---------------------------------------------------------------- pieces
    /// <summary>The state dot, vertically centred on a row <paramref name="rowHeight"/> tall.</summary>
    private static void StatusDot(float rowHeight)
    {
        var radius = MathF.Round(ImGui.GetTextLineHeight() * 0.25f);
        var pos = ImGui.GetCursorScreenPos();
        var center = new Vector2(pos.X + radius, MathF.Round(pos.Y + rowHeight * 0.5f));
        ImGui.GetWindowDrawList().AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(StateColor()));
        ImGui.Dummy(new Vector2(radius * 2, rowHeight));
    }

    private static void Chip(string label, string value, Vector4? valueColor, string tooltip)
    {
        using (ImRaii.Group())
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, label);
            ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X * 0.5f);
            ImGuiEx.Text(valueColor ?? ImGuiColors.DalamudWhite, value);
        }
        ImGuiEx.Tooltip(tooltip);
    }

    private static Vector4 StateColor()
    {
        if (Controller.Paused) return ImGuiColors.DalamudOrange;
        return Controller.State switch
        {
            FarmState.InFate or FarmState.CollectTurnIn or FarmState.ClearingAggro => ImGuiColors.HealerGreen,
            FarmState.SelectingZone or FarmState.TravelingToZone or FarmState.SelectingFate
                or FarmState.TravelingToFate or FarmState.Finishing => ImGuiColors.TankBlue,
            FarmState.Maintenance or FarmState.ChocoboLeveling or FarmState.GemstoneShopping => ImGuiColors.DalamudYellow,
            FarmState.Stopped => ImGuiColors.DalamudRed,
            _ => ImGuiColors.DalamudGrey,
        };
    }

    // ---------------------------------------------------------------- state
    private void TrackPosition()
    {
        if (C.OverlayLocked) return;
        var pos = ImGui.GetWindowPos();
        if (Vector2.DistanceSquared(pos, C.OverlayPosition) > 1f)
        {
            C.OverlayPosition = pos;
            _positionDirty = true;
        }
        else if (_positionDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _positionDirty = false;
            C.Save();
        }
    }

    private void Sample()
    {
        var now = Environment.TickCount64;
        if (now - _lastSampleMs < SampleIntervalMs) return;
        _lastSampleMs = now;
        _heldGems = Features.InventoryUtil.GetGemstoneCount();
        _goal = ComputeGoal();
    }

    /// <summary>
    /// The one number this run is working towards, if it has one. Mode progress wins over the
    /// generic stop triggers, since that's what the user picked the mode for.
    /// </summary>
    private static Goal? ComputeGoal()
    {
        var c = C;
        var stats = Controller.Stats;
        var territory = Svc.ClientState.TerritoryType;

        switch (c.Mode)
        {
            case FarmingMode.SharedFates:
            {
                var zone = Features.SharedFateTracker.GetZone(territory);
                if (zone is { } z)
                    return new Goal($"Shared FATEs in {z.ZoneName}", z.Completed,
                        Features.SharedFateTracker.ZoneProgress.Total);
                break;
            }

            case FarmingMode.Atma:
            case FarmingMode.Demiatma:
            case FarmingMode.LuminousCrystals:
            case FarmingMode.Memories:
            {
                var items = Data.CollectionRequirements.ForMode(c.Mode);
                if (items.Length == 0) break;
                var done = items.Count(i => Features.InventoryUtil.GetItemCount(i.ItemId) >= i.Required);
                return new Goal("Collectables", done, items.Length);
            }

            case FarmingMode.Manual:
            {
                var entry = c.ManualZones.FirstOrDefault(z => z.TerritoryId == territory && z.FatesToRun > 0);
                if (entry != null)
                    return new Goal($"FATEs in {entry.Name}", Math.Min(entry.FatesDone, entry.FatesToRun), entry.FatesToRun);
                break;
            }
        }

        if (c.StopAtLevel && c.DesiredLevel > stats.StartLevel)
            return new Goal($"Level {c.DesiredLevel}", stats.CurrentLevel - stats.StartLevel,
                c.DesiredLevel - stats.StartLevel);

        if (c.StopAtGemstoneCount && c.GemstoneStopCount > 0)
            return new Goal("Gemstones earned", stats.GemstonesGained, c.GemstoneStopCount);

        return null;
    }
}

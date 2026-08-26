using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;

namespace Autofate.UI;

public sealed partial class MainWindow
{
    private void DrawStatusTab()
    {
        // Surface a failed-Start error (missing required plugin) in red at the very top.
        if (!string.IsNullOrEmpty(Controller.LastStartError))
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.95f, 0.3f, 0.3f, 1f),
                $"Cannot start: {Controller.LastStartError}");
            ImGui.TextColored(new System.Numerics.Vector4(0.95f, 0.3f, 0.3f, 1f),
                "Install/enable the missing plugin (see detection list below), then press START again.");
            ImGui.Separator();
        }

        var s = Controller.Stats;
        ImGui.TextUnformatted($"Running: {Controller.Running}");
        ImGui.TextUnformatted($"State: {Controller.State}");
        ImGui.TextUnformatted($"Status: {Controller.StatusText}");
        ImGui.TextUnformatted($"Last stop reason: {Controller.LastStopReason}");
        ImGui.Separator();

        ImGui.TextUnformatted($"Runtime: {s.Runtime:hh\\:mm\\:ss}");
        ImGui.TextUnformatted($"FATEs completed: {s.FatesCompleted}");
        ImGui.TextUnformatted($"FATEs attempted: {s.FatesAttempted}");
        ImGui.TextUnformatted($"Gemstones gained (gross): {s.GemstonesGained}");
        ImGui.TextUnformatted($"Level: {s.StartLevel} -> {s.CurrentLevel}");

        ImGui.Separator();
        ImGui.TextUnformatted("Current zone FATEs:");
        var candidates = Core.FateSelector.GetCandidates(C);
        if (candidates.Count == 0)
            ImGui.TextDisabled("None in range/criteria.");
        foreach (var c in candidates)
        {
            var fate = c.Fate;
            ImGui.BulletText($"{fate.Name} | {c.Type} | Lv{fate.Level} | {fate.Progress}% | {c.TimeRemaining}s | {c.Distance:0}y");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Installed plugin detection:");
        DrawDetect("vnavmesh", IPC.NavmeshIPC.IsInstalled);
        DrawDetect("Lifestream", IPC.LifestreamIPC.IsInstalled);
        DrawDetect(IPC.BossModIPC.DisplayName, IPC.BossModIPC.IsInstalled);
        DrawDetect("Wrath Combo", IPC.WrathComboIPC.IsInstalled);
        DrawDetect("Rotation Solver Reborn", IPC.RotationSolverIPC.IsInstalled);
        DrawDetect("TextAdvance", IPC.TextAdvanceIPC.IsInstalled);

        ImGui.Separator();
        DrawOverlaySettings();

        ImGui.Separator();
        var verbose = C.VerboseLogging;
        if (ImGui.Checkbox("Verbose logging", ref verbose)) { C.VerboseLogging = verbose; Save(); }
        ImGuiEx.HelpMarker("Logs detailed diagnostics to the Dalamud log (/xllog): fate selection, plus [Diag/...] lines for NPC interaction, combat targeting/movement, collect, and escort. Use when reporting a stuck/loop bug.");

        ImGui.Separator();
        // Reset all settings to defaults; Ctrl-gated to avoid misclicks.
        var ctrl = ImGui.GetIO().KeyCtrl;
        using (ImRaii.Disabled(!ctrl))
        {
            if (ImGui.Button("Reset config to defaults"))
            {
                Plugin.ResetToDefaults();
            }
        }
        if (!ctrl)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(hold Ctrl)");
        }
        ImGui.TextDisabled("Resets everything, including mount, food/potion, gemstone vendor, and stable setup.");
    }

    /// <summary>Settings for the small runtime overlay shown while farming.</summary>
    private void DrawOverlaySettings()
    {
        ImGui.TextUnformatted("Runtime overlay");

        var useOverlay = C.UseRuntimeOverlay;
        if (ImGui.Checkbox("Show the overlay while farming", ref useOverlay)) { C.UseRuntimeOverlay = useOverlay; Save(); }
        ImGuiEx.HelpMarker("A small window with what the bot is doing, session stats, and pause / stop / settings buttons. Drag it anywhere.");

        using var disabled = ImRaii.Disabled(!useOverlay);

        var hide = C.OverlayHidesMainWindow;
        if (ImGui.Checkbox("Hide this window while farming", ref hide)) { C.OverlayHidesMainWindow = hide; Save(); }
        ImGuiEx.HelpMarker("The overlay's cog button brings it back without stopping the run.");

        var compact = C.OverlayCompact;
        if (ImGui.Checkbox("Compact overlay", ref compact)) { C.OverlayCompact = compact; Save(); }
        ImGuiEx.HelpMarker("One line: status text and the buttons, no stats.");

        var locked = C.OverlayLocked;
        if (ImGui.Checkbox("Lock overlay position", ref locked)) { C.OverlayLocked = locked; Save(); }

        var alpha = C.OverlayAlpha;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Overlay opacity", ref alpha, 0.2f, 1f, "%.2f")) C.OverlayAlpha = alpha;
        if (ImGui.IsItemDeactivatedAfterEdit()) Save();

        if (ImGui.Button("Reset overlay position")) Plugin.Instance!.ResetOverlayPosition();
    }

    private static void DrawDetect(string name, bool installed)
    {
        var col = installed ? new System.Numerics.Vector4(0.3f, 0.9f, 0.3f, 1f) : new System.Numerics.Vector4(0.7f, 0.3f, 0.3f, 1f);
        ImGui.Bullet();
        ImGui.SameLine();
        ImGuiEx.Text(col, $"{name}: {(installed ? "yes" : "no")}");
    }
}

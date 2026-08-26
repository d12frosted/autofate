using System.Linq;
using System.Numerics;
using Autofate.IPC;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Autofate.Core;

/// <summary>
/// Travels the player to a target territory. Prefers Lifestream; falls back to the game's Telepo
/// to the nearest aetheryte.
/// </summary>
public static unsafe class Teleporter
{
    public static bool IsInTerritory(uint territoryId) => Svc.ClientState.TerritoryType == territoryId;

    public static bool IsBusy()
    {
        if (LifestreamIPC.IsInstalled && LifestreamIPC.IsBusy()) return true;
        return Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
            || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51]
            || Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting];
    }

    /// <summary>Find the primary aetheryte row id for a territory (the main one, lowest order).</summary>
    public static uint FindAetheryteForTerritory(uint territoryId)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
            var match = sheet
                .Where(a => a.RowId != 0 && a.IsAetheryte && a.Territory.RowId == territoryId)
                .OrderBy(a => a.Order)
                .FirstOrDefault();
            return match.RowId;
        }
        catch { return 0; }
    }

    public static string? FindAetheryteName(uint territoryId)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
            var match = sheet
                .Where(a => a.RowId != 0 && a.IsAetheryte && a.Territory.RowId == territoryId)
                .OrderBy(a => a.Order)
                .FirstOrDefault();
            return match.PlaceName.ValueNullable?.Name.ToString();
        }
        catch { return null; }
    }

    /// <summary>
    /// Initiate travel to a territory. Returns true if we are already there. Otherwise issues a
    /// teleport (Lifestream or Telepo) and returns false; call again until <see cref="IsInTerritory"/>.
    /// </summary>
    public static bool TravelToTerritory(Configuration c, uint territoryId)
    {
        if (IsInTerritory(territoryId)) return true;
        if (IsBusy()) return false;
        if (!EzThrottler.Throttle("AF_Teleport", 8000)) return false;

        // Prefer Lifestream when enabled + installed.
        if (c.UseLifestream && LifestreamIPC.IsInstalled)
        {
            var name = FindAetheryteName(territoryId);
            if (!string.IsNullOrEmpty(name))
            {
                Svc.Log.Debug($"[Teleporter] Lifestream -> {name}");
                LifestreamIPC.TeleportToAetheryte(name!);
                return false;
            }
        }

        // Fallback: Telepo to the aetheryte id.
        var aetheryteId = FindAetheryteForTerritory(territoryId);
        if (aetheryteId == 0)
        {
            if (EzThrottler.Throttle("AF_NoAetheryte", 30000))
                Svc.Log.Warning($"[Teleporter] No aetheryte found for territory {territoryId}.");
            return false;
        }

        try
        {
            var telepo = Telepo.Instance();
            if (telepo != null)
            {
                Svc.Log.Debug($"[Teleporter] Telepo -> aetheryte {aetheryteId}");
                telepo->Teleport(aetheryteId, 0);
            }
        }
        catch (Exception e) { Svc.Log.Verbose($"[Teleporter] Teleport failed: {e.Message}"); }
        return false;
    }

    // ------------------------------------------------------------------ nearest aetheryte

    /// <summary>An attunable aetheryte with the world position it drops you at.</summary>
    public readonly record struct AetheryteInfo(uint RowId, string Name, Vector3 Position);

    // Aetheryte lists never change at runtime, so resolve each territory once.
    private static readonly Dictionary<uint, List<AetheryteInfo>> AetheryteCache = new();

    /// <summary>All main aetherytes in a territory, with world positions (Excel-backed, cached).</summary>
    public static IReadOnlyList<AetheryteInfo> AetherytesInTerritory(uint territoryId)
    {
        if (AetheryteCache.TryGetValue(territoryId, out var cached)) return cached;

        var list = new List<AetheryteInfo>();
        try
        {
            foreach (var a in Svc.Data.GetExcelSheet<Aetheryte>())
            {
                if (a.RowId == 0 || !a.IsAetheryte) continue;
                if (a.Territory.RowId != territoryId) continue;
                // The aetheryte's physical spot lives in the Level sheet (that's where teleporting
                // drops us). Skip rows without one rather than guessing from map markers.
                if (a.Level.Count == 0) continue;
                if (a.Level[0].ValueNullable is not { } lvl) continue;
                var pos = new Vector3(lvl.X, lvl.Y, lvl.Z);
                if (pos == Vector3.Zero) continue;
                var name = a.PlaceName.ValueNullable?.Name.ToString();
                list.Add(new AetheryteInfo(a.RowId, string.IsNullOrEmpty(name) ? $"Aetheryte #{a.RowId}" : name!, pos));
            }
        }
        catch (Exception e) { Svc.Log.Warning($"[Teleporter] Failed to read aetherytes for territory {territoryId}: {e.Message}"); }

        AetheryteCache[territoryId] = list;
        return list;
    }

    /// <summary>Is this aetheryte attuned? Unknown (API failure) counts as attuned: let the teleport decide.</summary>
    public static bool IsAttuned(uint aetheryteId)
    {
        try
        {
            var ui = UIState.Instance();
            return ui == null || ui->IsAetheryteUnlocked(aetheryteId);
        }
        catch { return true; }
    }

    /// <summary>
    /// The attuned aetheryte in <paramref name="territoryId"/> closest to <paramref name="target"/>,
    /// or null if the zone has none. <paramref name="allow"/> can veto individual aetherytes.
    /// </summary>
    public static AetheryteInfo? FindNearestAetheryte(uint territoryId, Vector3 target, Func<uint, bool>? allow = null)
    {
        AetheryteInfo? best = null;
        var bestDist = float.MaxValue;
        foreach (var a in AetherytesInTerritory(territoryId))
        {
            if (allow != null && !allow(a.RowId)) continue;
            if (!IsAttuned(a.RowId)) continue;
            var d = Vector3.Distance(a.Position, target);
            if (d >= bestDist) continue;
            best = a;
            bestDist = d;
        }
        return best;
    }

    /// <summary>
    /// Teleport to one specific aetheryte by row id. Always uses Telepo rather than Lifestream:
    /// row ids are exact, while a name lookup can match an aetheryte in another zone.
    /// </summary>
    public static bool TeleportToAetheryteId(uint aetheryteId)
    {
        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null) return false;
            // Teleport reads the entry (cost, ticket state) out of the teleport list, so make sure
            // the list is current before asking for one.
            telepo->UpdateAetheryteList();
            telepo->Teleport(aetheryteId, 0);
            return true;
        }
        catch (Exception e)
        {
            Svc.Log.Verbose($"[Teleporter] Teleport to aetheryte {aetheryteId} failed: {e.Message}");
            return false;
        }
    }

    /// <summary>Run an arbitrary Lifestream command (e.g. go home for chocobo stabling).</summary>
    public static void LifestreamCommand(string command)
    {
        if (LifestreamIPC.IsInstalled)
            LifestreamIPC.ExecuteCommand(command);
        else
            Svc.Log.Warning("[Teleporter] Lifestream not installed; cannot run command: " + command);
    }
}

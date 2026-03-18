using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace OneVsFive;

public class OneVsFivePlugin : BasePlugin
{
    public override string ModuleName    => "1v5 Mode";
    public override string ModuleVersion => "1.3.0";
    public override string ModuleAuthor  => "toi";

    private CCSPlayerController?      _solo         = null;
    private string?                   _selectedMap  = null;
    private string?                   _selectedSite = null;
    private bool                      _matchStarted = false;
    private Dictionary<ulong, string> _votes        = new();

    private readonly List<string> _allowedMaps = new()
    {
        "de_mirage", "de_inferno", "de_nuke",
        "de_dust2",  "de_ancient", "de_anubis", "de_vertigo"
    };

    private static readonly HashSet<string> GrenadeClassNames = new()
    {
        "weapon_smokegrenade", "weapon_flashbang", "weapon_hegrenade",
        "weapon_molotov", "weapon_incgrenade", "weapon_decoy"
    };

    public override void Load(bool hotReload)
    {
        Server.PrintToChatAll(" [1v5] Plugin chargé !");
        AddTimer(75f, UxReminder, TimerFlags.REPEAT);
        RegisterEventHandler<EventPlayerTeam>      (OnPlayerTeam);
        RegisterEventHandler<EventRoundStart>      (OnRoundStart);
        RegisterEventHandler<EventRoundEnd>        (OnRoundEnd);
        RegisterEventHandler<EventPlayerSpawn>     (OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
    }

    [ConsoleCommand("!rules", "Règles du 1v5")]
    public void CommandRules(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null) return;
        player.PrintToChat(" SOLO : 16 000$ | 3 smokes + 2 flashs + 1 HE + 1 molo | +5HP/s");
        player.PrintToChat(" CT : pas de grenades | Premier à 13 rounds gagne");
    }

    [ConsoleCommand("!help", "Commandes")]
    public void CommandHelp(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null) return;
        player.PrintToChat(" !votesolo <nom> | !map <nom> | !site A/B/random | !ready");
    }

    [ConsoleCommand("!solo", "Solo actuel")]
    public void CommandSolo(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null) return;
        player.PrintToChat(_solo == null ? " Aucun solo." : $" SOLO : {_solo.PlayerName}");
    }

    [ConsoleCommand("!votesolo", "Voter pour un solo")]
    public void CommandVoteSolo(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || _matchStarted) return;
        var name = info.GetArg(1).Trim().ToLower();
        if (string.IsNullOrEmpty(name)) return;
        var target = FindPlayerByName(name);
        if (target == null) { player.PrintToChat($" Joueur introuvable."); return; }
        _votes[player.SteamID] = target.PlayerName;
        player.PrintToChat($" Vote pour {target.PlayerName}.");
        CheckVoteResult();
    }

    [ConsoleCommand("!map", "Choisir la map")]
    public void CommandMap(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsSolo(player) || _matchStarted) return;
        var input = info.GetArg(1).Trim().ToLower();
        var fullMap = input.StartsWith("de_") ? input : $"de_{input}";
        if (!_allowedMaps.Contains(fullMap)) { player!.PrintToChat(" Map non autorisée."); return; }
        _selectedMap = fullMap;
        Server.PrintToChatAll($" [1v5] Map : {fullMap}");
        CheckSetupComplete(player!);
    }

    [ConsoleCommand("!site", "Choisir le site")]
    public void CommandSite(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsSolo(player) || _matchStarted) return;
        var site = info.GetArg(1).Trim().ToUpper();
        if (site != "A" && site != "B" && site != "RANDOM") return;
        _selectedSite = site == "RANDOM" ? (new Random().Next(2) == 0 ? "A" : "B") : site;
        Server.PrintToChatAll($" [1v5] Site : {_selectedSite}");
        CheckSetupComplete(player!);
    }

    [ConsoleCommand("!ready", "Lancer la partie")]
    public void CommandReady(CCSPlayerController? player, CommandInfo _)
    {
        if (!IsSolo(player) || _matchStarted) return;
        if (_selectedMap == null) { player!.PrintToChat(" Choisis une map."); return; }
        if (_selectedSite == null) { player!.PrintToChat(" Choisis un site."); return; }
        StartMatch();
    }

    [ConsoleCommand("!restart", "Reset (admin)")]
    [RequiresPermissions("@css/root")]
    public void CommandRestart(CCSPlayerController? player, CommandInfo _)
    {
        ResetAll();
        Server.ExecuteCommand("mp_restartgame 3");
        Server.PrintToChatAll(" [ADMIN] Reset.");
    }

    [ConsoleCommand("!forcesolo", "Forcer solo (admin)")]
    [RequiresPermissions("@css/root")]
    public void CommandForceSolo(CCSPlayerController? player, CommandInfo info)
    {
        var target = FindPlayerByName(info.GetArg(1).Trim().ToLower());
        if (target == null) return;
        SetSolo(target);
    }

    [ConsoleCommand("!forcemap", "Forcer map (admin)")]
    [RequiresPermissions("@css/root")]
    public void CommandForceMap(CCSPlayerController? player, CommandInfo info)
    {
        var input = info.GetArg(1).Trim().ToLower();
        _selectedMap = input.StartsWith("de_") ? input : $"de_{input}";
        Server.PrintToChatAll($" [ADMIN] Map : {_selectedMap}");
    }

    [ConsoleCommand("!forcesite", "Forcer site (admin)")]
    [RequiresPermissions("@css/root")]
    public void CommandForceSite(CCSPlayerController? player, CommandInfo info)
    {
        _selectedSite = info.GetArg(1).Trim().ToUpper();
        Server.PrintToChatAll($" [ADMIN] Site : {_selectedSite}");
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        if (!_matchStarted) return HookResult.Continue;
        @event.Userid?.PrintToChat(" Changement d'équipe désactivé.");
        return HookResult.Handled;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (!_matchStarted) return HookResult.Continue;
        if (_solo != null && _solo.IsValid)
            AddTimer(0.5f, () => GiveSoloAdvantages(_solo));
        foreach (var ct in GetCTPlayers())
            AddTimer(0.6f, () => StripGrenades(ct));
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (!_matchStarted) return HookResult.Continue;
        var player = @event.Userid;
        if (player == null) return HookResult.Continue;
        if (IsSoloPlayer(player))
        {
            AddTimer(0.5f, () => GiveSoloAdvantages(player));
            StartSoloRegen(player);
        }
        else if (player.Team == CsTeam.CounterTerrorist)
            AddTimer(0.6f, () => StripGrenades(player));
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info) => HookResult.Continue;

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && _solo != null && player.SteamID == _solo.SteamID)
        {
            Server.PrintToChatAll(" [1v5] Solo déconnecté. Reset.");
            ResetAll();
            Server.ExecuteCommand("mp_restartgame 5");
        }
        return HookResult.Continue;
    }

    private void StartMatch()
    {
        _matchStarted = true;
        Server.ExecuteCommand("mp_maxrounds 26");
        Server.ExecuteCommand("mp_overtime_enable 0");
        Server.ExecuteCommand("mp_friendlyfire 0");
        Server.PrintToChatAll($" 1v5 START | SOLO : {_solo!.PlayerName} | {_selectedMap} | Site {_selectedSite}");
        Server.ExecuteCommand($"changelevel {_selectedMap}");
    }

    private void GiveSoloAdvantages(CCSPlayerController player)
    {
        if (!player.IsValid || !player.PawnIsAlive) return;
        if (player.InGameMoneyServices != null)
            player.InGameMoneyServices.Account = 16000;
        StripGrenades(player);
        player.GiveNamedItem("weapon_smokegrenade");
        player.GiveNamedItem("weapon_smokegrenade");
        player.GiveNamedItem("weapon_smokegrenade");
        player.GiveNamedItem("weapon_flashbang");
        player.GiveNamedItem("weapon_flashbang");
        player.GiveNamedItem("weapon_hegrenade");
        player.GiveNamedItem("weapon_molotov");
    }

    private void StartSoloRegen(CCSPlayerController player)
    {
        AddTimer(1f, () =>
        {
            if (!_matchStarted || !player.IsValid || !player.PawnIsAlive || !IsSoloPlayer(player)) return;
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || pawn.Health >= 100) return;
            pawn.Health = Math.Min(100, pawn.Health + 5);
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }, TimerFlags.REPEAT);
    }

    private void StripGrenades(CCSPlayerController player)
    {
        if (!player.IsValid || !player.PawnIsAlive) return;
        var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
        if (weapons == null) return;
        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon != null && GrenadeClassNames.Contains(weapon.DesignerName))
                weapon.Remove();
        }
    }

    private void CheckVoteResult()
    {
        var majority = (GetActivePlayers().Count / 2) + 1;
        var winner = _votes.Values.GroupBy(v => v)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).FirstOrDefault();
        if (winner != null && winner.Count >= majority)
        {
            var target = FindPlayerByName(winner.Name.ToLower());
            if (target != null) { SetSolo(target); _votes.Clear(); }
        }
    }

    private void SetSolo(CCSPlayerController player)
    {
        _solo = player;
        player.SwitchTeam(CsTeam.Terrorist);
        player.PrintToChat(" Tu es le SOLO. !map | !site | !ready");
        Server.PrintToChatAll($" [1v5] SOLO : {player.PlayerName}");
    }

    private void CheckSetupComplete(CCSPlayerController player)
    {
        if (_selectedMap != null && _selectedSite != null)
            player.PrintToChat(" Setup OK ! Lance avec !ready");
    }

    private void ResetAll()
    {
        _solo = null; _selectedMap = null; _selectedSite = null;
        _matchStarted = false; _votes.Clear();
    }

    private void UxReminder()
    {
        if (_matchStarted) return;
        if (_solo == null) Server.PrintToChatAll(" [1v5] !votesolo <nom>");
        else if (_selectedMap == null || _selectedSite == null)
            Server.PrintToChatAll($" [1v5] {_solo.PlayerName} → !map | !site | !ready");
        else Server.PrintToChatAll($" [1v5] En attente de !ready ({_solo.PlayerName})");
    }

    private bool IsSolo(CCSPlayerController? player)
    {
        if (player == null) return false;
        if (_solo == null) { player.PrintToChat(" Aucun solo."); return false; }
        if (player.SteamID != _solo.SteamID) { player.PrintToChat(" Solo uniquement."); return false; }
        return true;
    }

    private bool IsSoloPlayer(CCSPlayerController? player) =>
        player != null && _solo != null && player.SteamID == _solo.SteamID;

    private List<CCSPlayerController> GetActivePlayers() =>
        Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot
            && p.Team != CsTeam.Spectator && p.Team != CsTeam.None).ToList();

    private List<CCSPlayerController> GetCTPlayers() =>
        Utilities.GetPlayers().Where(p => p.IsValid && p.Team == CsTeam.CounterTerrorist).ToList();

    private CCSPlayerController? FindPlayerByName(string nameLower) =>
        Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.PlayerName.ToLower().Contains(nameLower));
}
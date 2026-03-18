using System.Drawing;
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
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor  => "toi";

    // ────────────────────────────────────────────────────────────────
    //  ÉTAT
    // ────────────────────────────────────────────────────────────────
    private CCSPlayerController?      _solo         = null;
    private string?                   _selectedMap  = null;
    private string?                   _selectedSite = null;
    private bool                      _matchStarted = false;
    private Dictionary<ulong, string> _votes        = new();

    // Timer xray — tourne en continu tant que le solo est vivant
    private CounterStrikeSharp.API.Modules.Timers.Timer? _xrayTimer = null;

    private readonly List<string> _allowedMaps = new()
    {
        "de_mirage", "de_inferno", "de_nuke",
        "de_dust2",  "de_ancient", "de_anubis", "de_vertigo"
    };

    private static readonly HashSet<string> GrenadeClassNames = new()
    {
        "weapon_smokegrenade",
        "weapon_flashbang",
        "weapon_hegrenade",
        "weapon_molotov",
        "weapon_incgrenade",
        "weapon_decoy"
    };

    // ────────────────────────────────────────────────────────────────
    //  LOAD
    // ────────────────────────────────────────────────────────────────
    public override void Load(bool hotReload)
    {
        Server.PrintToChatAll(" [1v5] Plugin chargé — en attente de 6 joueurs.");
        AddTimer(75f, UxReminder, TimerFlags.REPEAT);

        RegisterEventHandler<EventPlayerTeam>      (OnPlayerTeam);
        RegisterEventHandler<EventRoundStart>      (OnRoundStart);
        RegisterEventHandler<EventRoundEnd>        (OnRoundEnd);
        RegisterEventHandler<EventPlayerSpawn>     (OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDeath>     (OnPlayerDeath);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
    }

    // ────────────────────────────────────────────────────────────────
    //  COMMANDES GLOBALES
    // ────────────────────────────────────────────────────────────────

    [ConsoleCommand("!rules", "Règles du 1v5")]
    public void CommandRules(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null) return;
        player.PrintToChat(" ══ RÈGLES 1v5 ══");
        player.PrintToChat(" SOLO (T) : 16 000$ | 3 smokes + 2 flashs + 1 HE + 1 molotov | +5HP/s | XRAY permanent");
        player.PrintToChat(" STACK (CT) : Budget normal | Pas de grenades | Pas de kit");
        player.PrintToChat(" Premier à 13 rounds gagne — Pas d'overtime");
        player.PrintToChat(" Solo choisit map (!map) et site (!site) puis !ready");
    }

    [ConsoleCommand("!help", "Commandes disponibles")]
    public void CommandHelp(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null) return;
        player.PrintToChat(" ══ COMMANDES 1v5 ══");
        player.PrintToChat(" !rules | !solo | !votesolo <nom>");
        player.PrintToChat(" [SOLO] !map <nom> | !site <A/B/random> | !ready");
        player.PrintToChat(" [ADMIN] !restart | !forcesolo <nom> | !forcemap <map> | !forcesite <A/B>");
    }

    [ConsoleCommand("!solo", "Affiche le solo actuel")]
    public void CommandSolo(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null) return;
        player.PrintToChat(_solo == null
            ? " Aucun solo. Vote avec !votesolo <nom>"
            : $" SOLO : {_solo.PlayerName}");
    }

    [ConsoleCommand("!votesolo", "Voter pour un solo")]
    public void CommandVoteSolo(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return;
        if (_matchStarted) { player.PrintToChat(" Vote impossible : partie en cours."); return; }

        var name = info.GetArg(1).Trim().ToLower();
        if (string.IsNullOrEmpty(name)) { player.PrintToChat(" Usage : !votesolo <nom>"); return; }

        var target = FindPlayerByName(name);
        if (target == null) { player.PrintToChat($" Joueur '{name}' introuvable."); return; }

        _votes[player.SteamID] = target.PlayerName;
        player.PrintToChat($" Vote enregistré pour {target.PlayerName}.");
        CheckVoteResult();
    }

    // ────────────────────────────────────────────────────────────────
    //  COMMANDES SOLO
    // ────────────────────────────────────────────────────────────────

    [ConsoleCommand("!map", "Choisir la map (solo uniquement)")]
    public void CommandMap(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsSolo(player)) return;
        if (_matchStarted) { player!.PrintToChat(" Partie déjà lancée."); return; }

        var input   = info.GetArg(1).Trim().ToLower();
        var fullMap = input.StartsWith("de_") ? input : $"de_{input}";

        if (!_allowedMaps.Contains(fullMap))
        {
            player!.PrintToChat($" Map '{input}' non autorisée. Dispo : mirage, inferno, nuke, dust2, ancient, anubis, vertigo");
            return;
        }

        _selectedMap = fullMap;
        Server.PrintToChatAll($" [1v5] Map choisie par le solo : {fullMap}");
        CheckSetupComplete(player!);
    }

    [ConsoleCommand("!site", "Choisir le site (solo uniquement)")]
    public void CommandSite(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsSolo(player)) return;
        if (_matchStarted) { player!.PrintToChat(" Partie déjà lancée."); return; }

        var site = info.GetArg(1).Trim().ToUpper();
        if (site != "A" && site != "B" && site != "RANDOM")
        {
            player!.PrintToChat(" Usage : !site A | !site B | !site random");
            return;
        }

        _selectedSite = site == "RANDOM" ? (new Random().Next(2) == 0 ? "A" : "B") : site;
        Server.PrintToChatAll($" [1v5] Site choisi par le solo : {_selectedSite}");
        CheckSetupComplete(player!);
    }

    [ConsoleCommand("!ready", "Lancer la partie (solo uniquement)")]
    public void CommandReady(CCSPlayerController? player, CommandInfo _)
    {
        if (!IsSolo(player)) return;
        if (_matchStarted)    { player!.PrintToChat(" Partie déjà lancée."); return; }
        if (_selectedMap  == null) { player!.PrintToChat(" Choisis une map : !map <nom>"); return; }
        if (_selectedSite == null) { player!.PrintToChat(" Choisis un site : !site A/B/random"); return; }

        if (GetActivePlayers().Count < 6)
        {
            player!.PrintToChat($" Pas assez de joueurs ({GetActivePlayers().Count}/6).");
            return;
        }

        StartMatch();
    }

    // ────────────────────────────────────────────────────────────────
    //  COMMANDES ADMIN
    // ────────────────────────────────────────────────────────────────

    [ConsoleCommand("!restart", "Reset le match (admin)")]
    [RequiresPermissions("@css/root")]
    public void CommandRestart(CCSPlayerController? player, CommandInfo _)
    {
        ResetAll();
        Server.ExecuteCommand("mp_restartgame 3");
        Server.PrintToChatAll(" [ADMIN] Match reset.");
    }

    [ConsoleCommand("!forcesolo", "Forcer un solo (admin)")]
    [RequiresPermissions("@css/root")]
    public void CommandForceSolo(CCSPlayerController? player, CommandInfo info)
    {
        var target = FindPlayerByName(info.GetArg(1).Trim().ToLower());
        if (target == null) { player?.PrintToChat(" Joueur introuvable."); return; }
        SetSolo(target);
        Server.PrintToChatAll($" [ADMIN] Solo forcé : {target.PlayerName}");
    }

    [ConsoleCommand("!forcemap", "Forcer une map (admin)")]
    [RequiresPermissions("@css/root")]
    public void CommandForceMap(CCSPlayerController? player, CommandInfo info)
    {
        var input    = info.GetArg(1).Trim().ToLower();
        _selectedMap = input.StartsWith("de_") ? input : $"de_{input}";
        Server.PrintToChatAll($" [ADMIN] Map forcée : {_selectedMap}");
    }

    [ConsoleCommand("!forcesite", "Forcer un site (admin)")]
    [RequiresPermissions("@css/root")]
    public void CommandForceSite(CCSPlayerController? player, CommandInfo info)
    {
        _selectedSite = info.GetArg(1).Trim().ToUpper();
        Server.PrintToChatAll($" [ADMIN] Site forcé : {_selectedSite}");
    }

    // ────────────────────────────────────────────────────────────────
    //  EVENTS
    // ────────────────────────────────────────────────────────────────

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        if (!_matchStarted) return HookResult.Continue;
        @event.Userid?.PrintToChat(" Changement d'équipe désactivé pendant la partie.");
        return HookResult.Handled;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (!_matchStarted) return HookResult.Continue;

        // Solo : argent + grenades au début du round
        if (_solo != null && _solo.IsValid)
            AddTimer(0.5f, () => GiveSoloAdvantages(_solo));

        // CT : strip grenades
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
            // Argent + grenades + lancement du xray loop continu
            AddTimer(0.5f, () =>
            {
                GiveSoloAdvantages(player);
                StartXrayLoop(player);   // Démarre / redémarre le loop xray
            });

            // Regen HP
            StartSoloRegen(player);
        }
        else if (player.Team == CsTeam.CounterTerrorist)
        {
            AddTimer(0.6f, () => StripGrenades(player));
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!_matchStarted) return HookResult.Continue;

        // Si le solo meurt → stop le loop xray et clear le glow
        if (IsSoloPlayer(@event.Userid))
        {
            StopXrayLoop();
            DisableAllGlow();
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        // Fin de round → stop xray proprement (sera relancé au prochain spawn)
        StopXrayLoop();
        DisableAllGlow();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null) return HookResult.Continue;

        if (_solo != null && player.SteamID == _solo.SteamID)
        {
            Server.PrintToChatAll(" [1v5] Le solo s'est déconnecté. Match reset.");
            ResetAll();
            Server.ExecuteCommand("mp_restartgame 5");
        }

        return HookResult.Continue;
    }

    // ────────────────────────────────────────────────────────────────
    //  DÉMARRAGE DU MATCH
    // ────────────────────────────────────────────────────────────────

    private void StartMatch()
    {
        _matchStarted = true;

        Server.ExecuteCommand("mp_maxrounds 26");
        Server.ExecuteCommand("mp_overtime_enable 0");
        Server.ExecuteCommand("mp_friendlyfire 0");
        Server.ExecuteCommand("mp_buy_anywhere 0");
        Server.ExecuteCommand("mp_buytime 20");
        Server.ExecuteCommand("sv_cheats 0");

        Server.PrintToChatAll(" ══════════════════════════════");
        Server.PrintToChatAll($" 1v5 — MATCH START");
        Server.PrintToChatAll($" SOLO : {_solo!.PlayerName}");
        Server.PrintToChatAll($" Map  : {_selectedMap}  |  Site : {_selectedSite}");
        Server.PrintToChatAll(" T : 16 000$ + grenades + XRAY permanent");
        Server.PrintToChatAll(" CT : pas de grenades, pas de kit");
        Server.PrintToChatAll(" ══════════════════════════════");

        Server.ExecuteCommand($"changelevel {_selectedMap}");
    }

    // ────────────────────────────────────────────────────────────────
    //  SOLO — ARGENT + GRENADES
    //  Strip d'abord pour être exact : 3 smokes, 2 flashs, 1 HE, 1 molo
    // ────────────────────────────────────────────────────────────────

    private void GiveSoloAdvantages(CCSPlayerController player)
    {
        if (!player.IsValid || !player.PawnIsAlive) return;

        // Argent
        if (player.InGameMoneyServices != null)
            player.InGameMoneyServices.Account = 16000;

        // Strip les grenades existantes d'abord pour ne pas dépiler
        StripGrenades(player);

        // Give exact : 3 smokes, 2 flashs, 1 HE, 1 molotov
        player.GiveNamedItem("weapon_smokegrenade");
        player.GiveNamedItem("weapon_smokegrenade");
        player.GiveNamedItem("weapon_smokegrenade");

        player.GiveNamedItem("weapon_flashbang");
        player.GiveNamedItem("weapon_flashbang");

        player.GiveNamedItem("weapon_hegrenade");

        player.GiveNamedItem("weapon_molotov");
    }

    // ────────────────────────────────────────────────────────────────
    //  SOLO — REGEN HP (+5 HP/s, max 100)
    // ────────────────────────────────────────────────────────────────

    private void StartSoloRegen(CCSPlayerController player)
    {
        AddTimer(1f, () => SoloRegenTick(player), TimerFlags.REPEAT);
    }

    private void SoloRegenTick(CCSPlayerController player)
    {
        if (!_matchStarted || !player.IsValid || !player.PawnIsAlive) return;
        if (!IsSoloPlayer(player)) return;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;

        if (pawn.Health < 100)
        {
            pawn.Health = Math.Min(100, pawn.Health + 5);
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  XRAY — Loop continu à 0.5s
    //  Re-applique le glow rouge sur tous les CT vivants tant que
    //  le solo est en vie. Couvre les respawns CT et changements d'état.
    // ────────────────────────────────────────────────────────────────

    private void StartXrayLoop(CCSPlayerController soloPlayer)
    {
        StopXrayLoop(); // Arrête un éventuel loop précédent

        _xrayTimer = AddTimer(0.5f, () =>
        {
            // Conditions d'arrêt automatique
            if (!_matchStarted || !soloPlayer.IsValid || !soloPlayer.PawnIsAlive)
            {
                DisableAllGlow();
                StopXrayLoop();
                return;
            }

            // Ré-appliquer le glow sur chaque CT vivant
            foreach (var ct in GetCTPlayers())
            {
                if (!ct.IsValid) continue;
                var pawn = ct.PlayerPawn.Value;
                if (pawn == null || !ct.PawnIsAlive) continue;

                pawn.Glow.GlowColorOverride = Color.FromArgb(255, 255, 50, 50); // Rouge vif
                pawn.Glow.GlowType          = 3;      // 3 = visible through walls
                pawn.Glow.GlowRange         = 9999;
                pawn.Glow.GlowRangeMin      = 0;
                pawn.Glow.GlowTeam          = (int)CsTeam.Terrorist; // Visible T seulement

                Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_Glow");
            }

        }, TimerFlags.REPEAT);
    }

    private void StopXrayLoop()
    {
        _xrayTimer?.Kill();
        _xrayTimer = null;
    }

    private void DisableAllGlow()
    {
        foreach (var player in Utilities.GetPlayers()
                     .Where(p => p.IsValid && p.PlayerPawn.Value != null))
        {
            var pawn = player.PlayerPawn.Value!;
            pawn.Glow.GlowType = 0;
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_Glow");
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  STRIP GRENADES — méthode stable sans RemoveWeapons() crash
    // ────────────────────────────────────────────────────────────────

    private void StripGrenades(CCSPlayerController player)
    {
        if (!player.IsValid || !player.PawnIsAlive) return;

        var pawn    = player.PlayerPawn.Value;
        var weapons = pawn?.WeaponServices?.MyWeapons;
        if (weapons == null) return;

        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon == null) continue;
            if (GrenadeClassNames.Contains(weapon.DesignerName))
                weapon.Remove();
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  VOTE SOLO
    // ────────────────────────────────────────────────────────────────

    private void CheckVoteResult()
    {
        var players  = GetActivePlayers();
        var majority = (players.Count / 2) + 1;

        var winner = _votes.Values
            .GroupBy(v => v)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();

        if (winner != null && winner.Count >= majority)
        {
            var target = FindPlayerByName(winner.Name.ToLower());
            if (target != null)
            {
                SetSolo(target);
                _votes.Clear();
                Server.PrintToChatAll($" [1v5] Vote terminé ! SOLO : {target.PlayerName}");
                Server.PrintToChatAll($" {target.PlayerName} → !map <nom>  |  !site A/B/random  |  !ready");
            }
        }
    }

    private void SetSolo(CCSPlayerController player)
    {
        _solo = player;
        player.SwitchTeam(CsTeam.Terrorist);
        player.PrintToChat(" Tu es le SOLO. !map <nom>  |  !site A/B/random  |  puis !ready");
    }

    private void CheckSetupComplete(CCSPlayerController player)
    {
        if (_selectedMap != null && _selectedSite != null)
            player.PrintToChat(" Setup complet ! Lance avec !ready quand tu es prêt.");
    }

    // ────────────────────────────────────────────────────────────────
    //  RESET COMPLET
    // ────────────────────────────────────────────────────────────────

    private void ResetAll()
    {
        StopXrayLoop();
        DisableAllGlow();
        _solo         = null;
        _selectedMap  = null;
        _selectedSite = null;
        _matchStarted = false;
        _votes.Clear();
    }

    // ────────────────────────────────────────────────────────────────
    //  UX REMINDER (toutes les 75s)
    // ────────────────────────────────────────────────────────────────

    private void UxReminder()
    {
        if (_matchStarted) return;

        if (_solo == null)
            Server.PrintToChatAll(" [1v5] !rules | !votesolo <nom> | En attente du solo...");
        else if (_selectedMap == null || _selectedSite == null)
            Server.PrintToChatAll($" [1v5] SOLO : {_solo.PlayerName} → !map <nom> | !site A/B/random");
        else
            Server.PrintToChatAll($" [1v5] En attente de !ready du solo ({_solo.PlayerName})");
    }

    // ────────────────────────────────────────────────────────────────
    //  HELPERS
    // ────────────────────────────────────────────────────────────────

    /// Vérifie que le joueur est le solo + affiche un message d'erreur sinon
    private bool IsSolo(CCSPlayerController? player)
    {
        if (player == null) return false;
        if (_solo == null)
        {
            player.PrintToChat(" Aucun solo défini.");
            return false;
        }
        if (player.SteamID != _solo.SteamID)
        {
            player.PrintToChat(" Commande réservée au solo.");
            return false;
        }
        return true;
    }

    /// Vérification silencieuse (pour les events internes)
    private bool IsSoloPlayer(CCSPlayerController? player) =>
        player != null && _solo != null && player.SteamID == _solo.SteamID;

    private List<CCSPlayerController> GetActivePlayers() =>
        Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot
                     && p.Team != CsTeam.Spectator
                     && p.Team != CsTeam.None)
            .ToList();

    private List<CCSPlayerController> GetCTPlayers() =>
        Utilities.GetPlayers()
            .Where(p => p.IsValid && p.Team == CsTeam.CounterTerrorist)
            .ToList();

    private CCSPlayerController? FindPlayerByName(string nameLower) =>
        Utilities.GetPlayers()
            .FirstOrDefault(p => p.IsValid
                              && p.PlayerName.ToLower().Contains(nameLower));
}
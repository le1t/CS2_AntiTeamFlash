﻿using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Listeners;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CS2AntiTeamFlash;

public class AntiTeamFlashConfig : BasePluginConfig
{
    public override int Version { get; set; } = 1;

    /// <summary>
    /// Включение/выключение всего плагина.
    /// Допустимые значения: 0 (отключён), 1 (включён).
    /// </summary>
    [JsonPropertyName("css_antiteamflash_enabled")]
    public int Enabled { get; set; } = 1;

    /// <summary>
    /// Разрешить самоослепление (собственной флешкой).
    /// Допустимые значения: 0 (запрещено, ослепление блокируется), 1 (разрешено).
    /// </summary>
    [JsonPropertyName("css_antiteamflash_flashowner")]
    public int FlashOwner { get; set; } = 1;

    /// <summary>
    /// Длительность показа HUD-сообщений для игроков (в секундах).
    /// Допустимый диапазон: от 1.0 до 10.0.
    /// При превышении границ значение автоматически ограничивается.
    /// </summary>
    [JsonPropertyName("css_antiteamflash_hud_duration")]
    public float HudDuration { get; set; } = 3.0f;

    /// <summary>
    /// Время агрегации статистики для одной флешки (в секундах).
    /// За это время собираются все ослеплённые от одной гранаты.
    /// Допустимый диапазон: от 1.0 до 10.0.
    /// При превышении границ значение автоматически ограничивается.
    /// </summary>
    [JsonPropertyName("css_antiteamflash_flash_aggregation_time")]
    public float FlashAggregationTime { get; set; } = 3.0f;
}

[MinimumApiVersion(369)]
public class CS2AntiTeamFlash : BasePlugin, IPluginConfig<AntiTeamFlashConfig>
{
    public override string ModuleName => "CS2 AntiTeamFlash";
    public override string ModuleAuthor => "Fixed by le1t1337 + AI DeepSeek. Code logic by Jesewe";
    public override string ModuleVersion => "1.6";

    public required AntiTeamFlashConfig Config { get; set; }

    // Обновлённые оффсеты из нового дампа
    private const int m_flFlashDuration = 0xCE4;   // float32
    private const int m_flFlashMaxAlpha = 0xCE8;   // float32
    private const int m_blindUntilTime = 0xC18;    // GameTime_t

    private class FlashbangStats
    {
        public HashSet<int> TeammateVictims { get; set; } = [];
        public HashSet<int> EnemyVictims { get; set; } = [];
        public string AttackerName { get; set; } = string.Empty;
        public int AttackerTeam { get; set; }
        public Timer? CleanupTimer { get; set; }
    }

    private readonly Dictionary<int, FlashbangStats> _activeFlashes = [];
    private readonly Dictionary<int, string> _hudMessages = [];
    private readonly Dictionary<int, Timer> _messageTimers = [];

    private string GetTeamString(int teamNum) => teamNum switch
    {
        2 => "<font color='red'>[T]</font>",
        3 => "<font color='#00BFFF'>[CT]</font>",
        _ => "<font color='gray'>[SPEC]</font>"
    };

    public void OnConfigParsed(AntiTeamFlashConfig config)
    {
        config.Enabled = Math.Clamp(config.Enabled, 0, 1);
        config.FlashOwner = Math.Clamp(config.FlashOwner, 0, 1);
        config.HudDuration = Math.Clamp(config.HudDuration, 1.0f, 10.0f);
        config.FlashAggregationTime = Math.Clamp(config.FlashAggregationTime, 1.0f, 10.0f);
        Config = config;
    }

    public override void Load(bool isReload)
    {
        // Удаление старого конфига (если он лежал в старой папке без подчёркивания)
        string oldConfigPath = Path.Combine(Server.GameDirectory, "counterstrikesharp", "configs", "plugins", "CS2AntiTeamFlash.json");
        if (File.Exists(oldConfigPath))
        {
            try { File.Delete(oldConfigPath); } catch { }
        }

        // Команды
        AddCommand("css_antiteamflash_settings", "Показать текущие настройки", OnSettingsCommand);
        AddCommand("css_antiteamflash_reload", "Перезагрузить конфигурацию", OnReloadCommand);
        AddCommand("css_antiteamflash_setenabled", "Включить/выключить плагин (0/1)", OnSetEnabledCommand);
        AddCommand("css_antiteamflash_setflashowner", "Разрешить самоослепление (0/1)", OnSetFlashOwnerCommand);
        AddCommand("css_antiteamflash_sethudduration", "Установить длительность HUD (1.0-10.0)", OnSetHudDurationCommand);
        AddCommand("css_antiteamflash_setaggregationtime", "Установить время агрегации флешки (1.0-10.0)", OnSetAggregationTimeCommand);

        // События
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind, HookMode.Post);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);

        RegisterListener<Listeners.OnTick>(OnTick);
    }

    private bool IsValidHumanPlayer([NotNullWhen(true)] CCSPlayerController? player)
        => player?.IsValid == true && !player.IsBot && player.PlayerPawn?.IsValid == true && player.Connected == PlayerConnectedState.Connected;

    private bool IsValidBot([NotNullWhen(true)] CCSPlayerController? player)
        => player?.IsValid == true && player.IsBot && player.PlayerPawn?.IsValid == true && player.Connected == PlayerConnectedState.Connected;

    private bool IsValidAnyPlayer([NotNullWhen(true)] CCSPlayerController? player)
        => player?.IsValid == true && player.PlayerPawn?.IsValid == true && player.Connected == PlayerConnectedState.Connected;

    private void ResetPlayerFlashDirect(CCSPlayerController player)
    {
        if (player?.PlayerPawn?.Value == null) return;
        IntPtr pawnAddress = player.PlayerPawn.Value.Handle;
        Marshal.WriteInt32(pawnAddress + m_flFlashDuration, 0);
        Marshal.WriteInt32(pawnAddress + m_flFlashMaxAlpha, 0);
        Marshal.WriteInt32(pawnAddress + m_blindUntilTime, 0);
    }

    private void ShowHudMessage(int slot, string message, float duration)
    {
        if (_messageTimers.TryGetValue(slot, out var oldTimer))
        {
            oldTimer.Kill();
            _messageTimers.Remove(slot);
        }

        _hudMessages[slot] = message;
        var timer = AddTimer(duration, () =>
        {
            if (_hudMessages.TryGetValue(slot, out var currentMsg) && currentMsg == message)
                _hudMessages.Remove(slot);
            _messageTimers.Remove(slot);
        });
        _messageTimers[slot] = timer;
    }

    private void OnTick()
    {
        if (Config.Enabled == 0) return;
        foreach (var (slot, message) in _hudMessages.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player?.IsValid == true)
                player.PrintToCenterHtml(message);
            else
            {
                _hudMessages.Remove(slot);
                if (_messageTimers.TryGetValue(slot, out var timer))
                {
                    timer.Kill();
                    _messageTimers.Remove(slot);
                }
            }
        }
    }

    private void ClearHudMessages()
    {
        foreach (var timer in _messageTimers.Values)
            timer.Kill();
        _hudMessages.Clear();
        _messageTimers.Clear();
    }

    private void ShowTargetMessage(CCSPlayerController victim, FlashbangStats stats, float duration)
    {
        if (victim?.IsValid != true) return;
        string msg = $"Ослепление от <font color='yellow'>{GetTeamString(stats.AttackerTeam)} {stats.AttackerName}</font><br>" +
                     $"Противников: <font color='red'>{stats.EnemyVictims.Count}</font> | Тиммейтов: <font color='green'>{stats.TeammateVictims.Count}</font><br>" +
                     $"Длительность: <font color='white'>{duration:F1} сек.</font>";
        ShowHudMessage(victim.Slot, msg, Config.HudDuration);
    }

    private void ShowAttackerStats(CCSPlayerController attacker, FlashbangStats stats)
    {
        if (attacker?.IsValid != true || attacker.IsBot) return;
        string msg = $"Ваша Флешка:<br>" +
                     $"Противников: <font color='red'>{stats.EnemyVictims.Count}</font> | Тиммейтов: <font color='green'>{stats.TeammateVictims.Count}</font>";
        ShowHudMessage(attacker.Slot, msg, 3.0f);
    }

    private void ShowBlockedByTeammate(CCSPlayerController victim, string attackerName, int attackerTeam, int teammates, int enemies)
    {
        if (victim?.IsValid != true) return;
        string msg = $"Тиммейт <font color='yellow'>{GetTeamString(attackerTeam)} {attackerName}</font> ослепил вас под защитой. Его Флешка:<br>" +
                     $"Противников: <font color='red'>{enemies}</font> | Тиммейтов: <font color='green'>{teammates}</font>";
        ShowHudMessage(victim.Slot, msg, 3.0f);
    }

    private void StartCleanupTimer(int attackerId, FlashbangStats stats)
    {
        stats.CleanupTimer?.Kill();
        stats.CleanupTimer = AddTimer(Config.FlashAggregationTime, () =>
        {
            _activeFlashes.Remove(attackerId);
        });
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        foreach (var stats in _activeFlashes.Values)
            stats.CleanupTimer?.Kill();
        _activeFlashes.Clear();
        ClearHudMessages();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info) => HookResult.Continue;

    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        if (Config.Enabled == 0) return HookResult.Continue;

        var victim = @event.Userid;
        var attacker = @event.Attacker;
        float duration = @event.BlindDuration;

        if (!IsValidAnyPlayer(victim)) return HookResult.Continue;
        bool attackerIsValid = IsValidAnyPlayer(attacker);
        int attackerId = attacker?.UserId ?? -1;
        int victimId = victim.UserId ?? -1;

        if (!attackerIsValid)
        {
            string msg = $"Ослепление от <font color='yellow'>неизвестный</font><br>Длительность: <font color='white'>{duration:F1} сек.</font>";
            ShowHudMessage(victim.Slot, msg, Config.HudDuration);
            return HookResult.Continue;
        }

        bool sameTeam = attacker!.TeamNum == victim.TeamNum;
        bool isSelf = attacker.UserId == victim.UserId;

        if (!_activeFlashes.TryGetValue(attackerId, out var stats))
        {
            stats = new FlashbangStats
            {
                AttackerName = attacker.PlayerName ?? (attacker.IsBot ? "Бот" : "Неизвестный"),
                AttackerTeam = attacker.TeamNum
            };
            _activeFlashes[attackerId] = stats;
        }

        StartCleanupTimer(attackerId, stats);

        if (isSelf)
        {
            if (Config.FlashOwner == 1)
                ShowAttackerStats(victim, stats);
            else
            {
                ResetPlayerFlashDirect(victim);
                if (victim.IsValid && !victim.IsBot)
                {
                    string msg = $"Самоослепление отключено<br>" +
                                 $"Этой флешкой ослеплено:<br>" +
                                 $"Противников: <font color='red'>{stats.EnemyVictims.Count}</font> | Тиммейтов: <font color='green'>{stats.TeammateVictims.Count}</font>";
                    ShowHudMessage(victim.Slot, msg, 3.0f);
                }
            }
        }
        else if (sameTeam)
        {
            stats.TeammateVictims.Add(victimId);
            ResetPlayerFlashDirect(victim);
            if (victim.IsValid && !victim.IsBot)
                ShowBlockedByTeammate(victim, stats.AttackerName, stats.AttackerTeam, stats.TeammateVictims.Count, stats.EnemyVictims.Count);
            if (attacker.IsValid && !attacker.IsBot)
                ShowAttackerStats(attacker, stats);
        }
        else
        {
            stats.EnemyVictims.Add(victimId);
            ShowTargetMessage(victim, stats, duration);
            if (attacker.IsValid && !attacker.IsBot)
                ShowAttackerStats(attacker, stats);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player?.IsValid == true)
        {
            int userId = player.UserId ?? -1;
            int slot = player.Slot;
            if (_activeFlashes.TryGetValue(userId, out var stats))
            {
                stats.CleanupTimer?.Kill();
                _activeFlashes.Remove(userId);
            }
            if (_messageTimers.TryGetValue(slot, out var timer))
            {
                timer.Kill();
                _messageTimers.Remove(slot);
            }
            _hudMessages.Remove(slot);
        }
        return HookResult.Continue;
    }

    // ---------- Команды ----------
    private void OnSettingsCommand(CCSPlayerController? player, CommandInfo command)
    {
        string enabledStatus = Config.Enabled == 1 ? "Включён" : "Отключён";
        string flashOwnerStatus = Config.FlashOwner == 1 ? "Разрешено" : "Запрещено";
        int onlineCount = Utilities.GetPlayers().Count(IsValidAnyPlayer);
        int activeFlashes = _activeFlashes.Count;

        string settings = $"""
            ================================================
            ТЕКУЩИЕ НАСТРОЙКИ {ModuleName} v{ModuleVersion}
            ================================================
            Плагин: {enabledStatus}
            Самоослепление: {flashOwnerStatus}
            Длительность HUD: {Config.HudDuration:F1} сек.
            Время агрегации: {Config.FlashAggregationTime:F1} сек.

            Активных игроков: {onlineCount}
            Активных флешек: {activeFlashes}
            ================================================
            """;
        command.ReplyToCommand(settings);
        if (player != null)
            player.PrintToChat(" [AntiTeamFlash] Настройки отправлены в консоль.");
    }

    private void OnReloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        try
        {
            string configPath = Path.Combine(Server.GameDirectory, "counterstrikesharp", "configs", "plugins", "CS2_AntiTeamFlash", "CS2_AntiTeamFlash.json");
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                var newConfig = System.Text.Json.JsonSerializer.Deserialize<AntiTeamFlashConfig>(json);
                if (newConfig != null)
                    OnConfigParsed(newConfig);
                SaveConfig();
            }
            else
                SaveConfig();

            foreach (var stats in _activeFlashes.Values)
                stats.CleanupTimer?.Kill();
            _activeFlashes.Clear();
            ClearHudMessages();

            command.ReplyToCommand("[AntiTeamFlash] Конфигурация перезагружена.");
        }
        catch
        {
            command.ReplyToCommand("[AntiTeamFlash] Ошибка при перезагрузке конфига.");
        }
    }

    private void OnSetEnabledCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            string help = $"""
                [AntiTeamFlash] Настройка: css_antiteamflash_enabled
                Описание: Включение/выключение всего плагина.
                Допустимые значения: 0 (отключён), 1 (включён).
                Текущее значение: {Config.Enabled}
                Использование: css_antiteamflash_setenabled <0/1>
                Пример: css_antiteamflash_setenabled 1
                """;
            command.ReplyToCommand(help);
            return;
        }

        string arg = command.GetArg(1);
        if (int.TryParse(arg, out int value) && (value == 0 || value == 1))
        {
            int old = Config.Enabled;
            Config.Enabled = value;
            SaveConfig();
            command.ReplyToCommand($"[AntiTeamFlash] enabled изменён с {old} на {value}.");
            if (Config.Enabled == 0)
                ClearHudMessages();
        }
        else
            command.ReplyToCommand("[AntiTeamFlash] Неверное значение. Используйте 0 или 1.");
    }

    private void OnSetFlashOwnerCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            string help = $"""
                [AntiTeamFlash] Настройка: css_antiteamflash_flashowner
                Описание: Разрешить самоослепление (собственной флешкой).
                Допустимые значения: 0 (запрещено, ослепление блокируется), 1 (разрешено).
                Текущее значение: {Config.FlashOwner}
                Использование: css_antiteamflash_setflashowner <0/1>
                Пример: css_antiteamflash_setflashowner 0
                """;
            command.ReplyToCommand(help);
            return;
        }

        string arg = command.GetArg(1);
        if (int.TryParse(arg, out int value) && (value == 0 || value == 1))
        {
            int old = Config.FlashOwner;
            Config.FlashOwner = value;
            SaveConfig();
            command.ReplyToCommand($"[AntiTeamFlash] flashowner изменён с {old} на {value}.");
        }
        else
            command.ReplyToCommand("[AntiTeamFlash] Неверное значение. Используйте 0 или 1.");
    }

    private void OnSetHudDurationCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            string help = $"""
                [AntiTeamFlash] Настройка: css_antiteamflash_hud_duration
                Описание: Длительность показа HUD-сообщений для игроков (в секундах).
                Допустимый диапазон: от 1.0 до 10.0.
                Текущее значение: {Config.HudDuration:F1}
                Использование: css_antiteamflash_sethudduration <1.0-10.0>
                Пример: css_antiteamflash_sethudduration 5.0
                """;
            command.ReplyToCommand(help);
            return;
        }

        string arg = command.GetArg(1).Replace(',', '.');
        if (float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
        {
            float old = Config.HudDuration;
            Config.HudDuration = Math.Clamp(value, 1.0f, 10.0f);
            SaveConfig();
            command.ReplyToCommand($"[AntiTeamFlash] hud_duration изменён с {old:F1} на {Config.HudDuration:F1}.");
        }
        else
            command.ReplyToCommand("[AntiTeamFlash] Неверное значение. Введите число с точкой (например 4.5).");
    }

    private void OnSetAggregationTimeCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            string help = $"""
                [AntiTeamFlash] Настройка: css_antiteamflash_flash_aggregation_time
                Описание: Время агрегации статистики для одной флешки (в секундах). За это время собираются все ослеплённые от одной гранаты.
                Допустимый диапазон: от 1.0 до 10.0.
                Текущее значение: {Config.FlashAggregationTime:F1}
                Использование: css_antiteamflash_setaggregationtime <1.0-10.0>
                Пример: css_antiteamflash_setaggregationtime 4.0
                """;
            command.ReplyToCommand(help);
            return;
        }

        string arg = command.GetArg(1).Replace(',', '.');
        if (float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
        {
            float old = Config.FlashAggregationTime;
            Config.FlashAggregationTime = Math.Clamp(value, 1.0f, 10.0f);
            SaveConfig();
            command.ReplyToCommand($"[AntiTeamFlash] время агрегации изменено с {old:F1} на {Config.FlashAggregationTime:F1}.");
        }
        else
            command.ReplyToCommand("[AntiTeamFlash] Неверное значение. Введите число с точкой (например 3.0).");
    }

    private void SaveConfig()
    {
        try
        {
            string configPath = Path.Combine(Server.GameDirectory, "counterstrikesharp", "configs", "plugins", "CS2_AntiTeamFlash", "CS2_AntiTeamFlash.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            string json = System.Text.Json.JsonSerializer.Serialize(Config, options);
            File.WriteAllText(configPath, json);
        }
        catch { }
    }

    public override void Unload(bool hotReload)
    {
        foreach (var stats in _activeFlashes.Values)
            stats.CleanupTimer?.Kill();
        _activeFlashes.Clear();
        ClearHudMessages();
    }
}
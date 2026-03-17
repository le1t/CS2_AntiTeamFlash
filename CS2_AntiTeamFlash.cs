using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Listeners;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

// Разрешаем неоднозначность Timer
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CS2AntiTeamFlash;

public class AntiTeamFlashConfig : BasePluginConfig
{
    public override int Version { get; set; } = 1;

    /// <summary>
    /// Включение/выключение плагина.
    /// 0 - плагин отключён, 1 - плагин включён.
    /// </summary>
    [JsonPropertyName("css_antiteamflash_enabled")]
    public int Enabled { get; set; } = 1;

    /// <summary>
    /// Разрешить самоослепление (собственной флешкой).
    /// 0 - блокировать самоослепление, 1 - разрешить.
    /// </summary>
    [JsonPropertyName("css_antiteamflash_flashowner")]
    public int FlashOwner { get; set; } = 1;

    /// <summary>
    /// Уровень логирования.
    /// 0 - Trace, 1 - Debug, 2 - Information, 3 - Warning, 4 - Error, 5 - Critical.
    /// </summary>
    [JsonPropertyName("css_antiteamflash_loglevel")]
    public int LogLevel { get; set; } = 4;

    /// <summary>
    /// Длительность показа сообщений в HUD (секунды).
    /// Диапазон: 1.0 - 10.0.
    /// </summary>
    [JsonPropertyName("css_antiteamflash_hud_duration")]
    public float HudDuration { get; set; } = 3.0f;

    /// <summary>
    /// Время агрегации статистики для одной флешки (секунды).
    /// За это время собираются все ослеплённые от одной гранаты.
    /// Диапазон: 1.0 - 10.0.
    /// </summary>
    [JsonPropertyName("css_antiteamflash_flash_aggregation_time")]
    public float FlashAggregationTime { get; set; } = 3.0f;
}

[MinimumApiVersion(362)]
public class CS2AntiTeamFlash : BasePlugin, IPluginConfig<AntiTeamFlashConfig>
{
    public override string ModuleName => "CS2 AntiTeamFlash";
    public override string ModuleAuthor => "Fixed by le1t1337 + AI DeepSeek. Code logic by Franc1sco Franug";
    public override string ModuleVersion => "1.5";

    public required AntiTeamFlashConfig Config { get; set; }

    // Серверные смещения из server_dll.cs (класс CCSPlayerPawnBase)
    private const int m_flFlashDuration = 0xE54;
    private const int m_flFlashMaxAlpha = 0xE58;
    private const int m_blindUntilTime = 0xD88;

    // Класс для хранения статистики одной флешки
    private class FlashbangStats
    {
        public HashSet<int> TeammateVictims { get; set; } = new();
        public HashSet<int> EnemyVictims { get; set; } = new();
        public string AttackerName { get; set; } = string.Empty;
        public int AttackerTeam { get; set; }
        public Timer? CleanupTimer { get; set; }
    }

    // Активные флешки (по UserId атакующего)
    private readonly Dictionary<int, FlashbangStats> _activeFlashes = new();

    // Система HUD сообщений
    private readonly Dictionary<int, string> _hudMessages = new();
    private readonly Dictionary<int, Timer> _messageTimers = new();

    private string GetTeamString(int teamNum)
    {
        return teamNum == 2 ? "<font color='red'>[T]</font>" : (teamNum == 3 ? "<font color='#00BFFF'>[CT]</font>" : "<font color='gray'>[SPEC]</font>");
    }

    public void OnConfigParsed(AntiTeamFlashConfig config)
    {
        config.Enabled = Math.Clamp(config.Enabled, 0, 1);
        config.FlashOwner = Math.Clamp(config.FlashOwner, 0, 1);
        config.LogLevel = Math.Clamp(config.LogLevel, 0, 5);
        config.HudDuration = Math.Clamp(config.HudDuration, 1.0f, 10.0f);
        config.FlashAggregationTime = Math.Clamp(config.FlashAggregationTime, 1.0f, 10.0f);
        Config = config;
    }

    public override void Load(bool isReload)
    {
        string oldConfigPath = Path.Combine(Server.GameDirectory, "counterstrikesharp", "configs", "plugins", "CS2AntiTeamFlash.json");
        if (File.Exists(oldConfigPath))
        {
            try
            {
                File.Delete(oldConfigPath);
                Log(LogLevel.Information, $"Старый конфиг удалён: {oldConfigPath}");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"Не удалось удалить старый конфиг: {ex.Message}");
            }
        }

        AddCommand("css_antiteamflash_help", "Показать справку по плагину", OnHelpCommand);
        AddCommand("css_antiteamflash_settings", "Показать текущие настройки", OnSettingsCommand);
        AddCommand("css_antiteamflash_test", "Тестовая команда", OnTestCommand);
        AddCommand("css_antiteamflash_reload", "Перезагрузить конфигурацию", OnReloadCommand);
        AddCommand("css_antiteamflash_setenabled", "Включить/выключить плагин (0/1)", OnSetEnabledCommand);
        AddCommand("css_antiteamflash_setflashowner", "Разрешить самоослепление (0/1)", OnSetFlashOwnerCommand);
        AddCommand("css_antiteamflash_setloglevel", "Установить уровень логирования (0-5)", OnSetLogLevelCommand);
        AddCommand("css_antiteamflash_sethudduration", "Установить длительность HUD (1.0-10.0)", OnSetHudDurationCommand);
        AddCommand("css_antiteamflash_setaggregationtime", "Установить время агрегации флешки (1.0-10.0)", OnSetAggregationTimeCommand);

        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind, HookMode.Post);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);

        RegisterListener<Listeners.OnTick>(OnTick);

        PrintInfo();

        if (isReload)
        {
            Server.NextFrame(() => Log(LogLevel.Information, "Горячая перезагрузка выполнена"));
        }
    }

    private void PrintInfo()
    {
        Log(LogLevel.Information, "===============================================");
        Log(LogLevel.Information, $"Плагин {ModuleName} версии {ModuleVersion} успешно загружен!");
        Log(LogLevel.Information, $"Автор: {ModuleAuthor}");
        Log(LogLevel.Information, "Текущие настройки:");
        Log(LogLevel.Information, $"  css_antiteamflash_enabled = {Config.Enabled} (0/1)");
        Log(LogLevel.Information, $"  css_antiteamflash_flashowner = {Config.FlashOwner} (0/1)");
        Log(LogLevel.Information, $"  css_antiteamflash_loglevel = {Config.LogLevel} (0-Trace, 1-Debug, 2-Information, 3-Warning, 4-Error, 5-Critical)");
        Log(LogLevel.Information, $"  css_antiteamflash_hud_duration = {Config.HudDuration:F1} сек.");
        Log(LogLevel.Information, $"  css_antiteamflash_flash_aggregation_time = {Config.FlashAggregationTime:F1} сек.");
        Log(LogLevel.Information, "===============================================");
    }

    private void Log(LogLevel level, string message)
    {
        if ((int)level >= Config.LogLevel)
            Logger.Log(level, "[AntiTeamFlash] {Message}", message);
    }

    private bool IsValidHumanPlayer([NotNullWhen(true)] CCSPlayerController? player)
    {
        return player != null &&
               player.IsValid &&
               !player.IsBot &&
               player.PlayerPawn != null &&
               player.PlayerPawn.IsValid &&
               player.PlayerPawn.Value != null &&
               player.Connected == PlayerConnectedState.PlayerConnected;
    }

    private bool IsValidBot([NotNullWhen(true)] CCSPlayerController? player)
    {
        return player != null &&
               player.IsValid &&
               player.IsBot &&
               player.PlayerPawn != null &&
               player.PlayerPawn.IsValid &&
               player.PlayerPawn.Value != null &&
               player.Connected == PlayerConnectedState.PlayerConnected;
    }

    private bool IsValidAnyPlayer([NotNullWhen(true)] CCSPlayerController? player)
    {
        return player != null &&
               player.IsValid &&
               player.PlayerPawn != null &&
               player.PlayerPawn.IsValid &&
               player.PlayerPawn.Value != null &&
               player.Connected == PlayerConnectedState.PlayerConnected;
    }

    private void ResetPlayerFlashDirect(CCSPlayerController player)
    {
        if (player?.PlayerPawn?.Value == null) return;

        IntPtr pawnAddress = player.PlayerPawn.Value.Handle;
        Marshal.WriteInt32(pawnAddress + m_flFlashDuration, 0);
        Marshal.WriteInt32(pawnAddress + m_flFlashMaxAlpha, 0);
        Marshal.WriteInt32(pawnAddress + m_blindUntilTime, 0);

        Log(LogLevel.Trace, $"Прямая запись в память для {player.PlayerName ?? "Бот"}: " +
            $"FlashDuration={Marshal.ReadInt32(pawnAddress + m_flFlashDuration)}, " +
            $"FlashMaxAlpha={Marshal.ReadInt32(pawnAddress + m_flFlashMaxAlpha)}, " +
            $"BlindUntilTime={Marshal.ReadInt32(pawnAddress + m_blindUntilTime)}");
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
            {
                _hudMessages.Remove(slot);
            }
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
            {
                player.PrintToCenterHtml(message);
            }
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
        {
            timer.Kill();
        }
        _hudMessages.Clear();
        _messageTimers.Clear();
    }

    private void ShowTargetMessage(CCSPlayerController victim, FlashbangStats stats, float duration)
    {
        if (victim?.IsValid != true) return;
        string attackerName = stats.AttackerName;
        string attackerTeamStr = GetTeamString(stats.AttackerTeam);
        int teammates = stats.TeammateVictims.Count;
        int enemies = stats.EnemyVictims.Count;
        string msg = $"Ослепление от <font color='yellow'>{attackerTeamStr} {attackerName}</font><br>" +
                     $"Противников: <font color='red'>{enemies}</font> | Тиммейтов: <font color='green'>{teammates}</font><br>" +
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
        string safeAttackerName = attackerName ?? "Неизвестный";
        string attackerTeamStr = GetTeamString(attackerTeam);
        string msg = $"Тиммейт <font color='yellow'>{attackerTeamStr} {safeAttackerName}</font> ослепил вас под защитой. Его Флешка:<br>" +
                     $"Противников: <font color='red'>{enemies}</font> | Тиммейтов: <font color='green'>{teammates}</font>";
        ShowHudMessage(victim.Slot, msg, 3.0f);
    }

    private void StartCleanupTimer(int attackerId, FlashbangStats stats)
    {
        stats.CleanupTimer?.Kill();
        stats.CleanupTimer = AddTimer(Config.FlashAggregationTime, () =>
        {
            _activeFlashes.Remove(attackerId);
            Log(LogLevel.Debug, $"Статистика для игрока {attackerId} удалена по таймеру");
        });
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        foreach (var stats in _activeFlashes.Values)
            stats.CleanupTimer?.Kill();
        _activeFlashes.Clear();
        ClearHudMessages();
        Log(LogLevel.Debug, "Статистика сброшена (новый раунд)");
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        return HookResult.Continue;
    }

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

        bool sameTeam = (attacker!.TeamNum == victim.TeamNum);
        bool isSelf = (attacker.UserId == victim.UserId);

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
            {
                ShowAttackerStats(victim, stats); // для самоослепления показываем статистику, как атакующему
            }
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
            {
                ShowBlockedByTeammate(victim, stats.AttackerName, stats.AttackerTeam, stats.TeammateVictims.Count, stats.EnemyVictims.Count);
            }
            if (attacker.IsValid && !attacker.IsBot)
            {
                ShowAttackerStats(attacker, stats);
            }
        }
        else
        {
            stats.EnemyVictims.Add(victimId);
            ShowTargetMessage(victim, stats, duration);
            if (attacker.IsValid && !attacker.IsBot)
            {
                ShowAttackerStats(attacker, stats);
            }
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
    private void OnHelpCommand(CCSPlayerController? player, CommandInfo command)
    {
        string help = $"""
            ================================================
            СПРАВКА ПО ПЛАГИНУ {ModuleName} v{ModuleVersion}
            ================================================
            ОПИСАНИЕ:
              Плагин предотвращает ослепление игроков флешками от своих же (тиммейтов).
              Поддерживает ботов.
              Отображает статистику ослеплений для каждой флешки (побросово, автоматический сброс),
              а также команду (CT/T) атакующего (синий для CT, красный для T).

            НАСТРОЙКИ:
              css_antiteamflash_enabled     - вкл/выкл плагина (0,1). По умолч. 1
              css_antiteamflash_flashowner  - разрешить самоослепление (0,1). По умолч. 1
              css_antiteamflash_loglevel    - уровень логов (0-Trace,1-Debug,2-Info,3-Warn,4-Error,5-Critical). По умолч. 4
              css_antiteamflash_hud_duration - длительность показа HUD (1.0-10.0). По умолч. 3.0
              css_antiteamflash_flash_aggregation_time - время агрегации для одной флешки (1.0-10.0). По умолч. 3.0

            КОМАНДЫ:
              css_antiteamflash_help         - справка
              css_antiteamflash_settings     - настройки и активные флешки
              css_antiteamflash_test         - тест (только для игроков)
              css_antiteamflash_reload       - перезагрузить конфиг
              css_antiteamflash_setenabled <0/1>
              css_antiteamflash_setflashowner <0/1>
              css_antiteamflash_setloglevel <0-5>
              css_antiteamflash_sethudduration <1.0-10.0>
              css_antiteamflash_setaggregationtime <1.0-10.0>

            ПРИМЕРЫ:
              css_antiteamflash_setenabled 1
              css_antiteamflash_setaggregationtime 4.0
            ================================================
            """;
        command.ReplyToCommand(help);
        if (player != null)
            player.PrintToChat(" [AntiTeamFlash] Справка отправлена в консоль.");
    }

    private void OnSettingsCommand(CCSPlayerController? player, CommandInfo command)
    {
        string enabledStatus = Config.Enabled == 1 ? "Включён" : "Отключён";
        string flashOwnerStatus = Config.FlashOwner == 1 ? "Разрешено" : "Запрещено";
        int onlineCount = Utilities.GetPlayers().Count(p => IsValidAnyPlayer(p));
        int activeFlashes = _activeFlashes.Count;

        string settings = $"""
            ================================================
            ТЕКУЩИЕ НАСТРОЙКИ {ModuleName} v{ModuleVersion}
            ================================================
            Плагин: {enabledStatus}
            Самоослепление: {flashOwnerStatus}
            Уровень логов: {Config.LogLevel} (0-Trace,1-Debug,2-Info,3-Warn,4-Error,5-Critical)
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

    private void OnTestCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null)
        {
            command.ReplyToCommand("[AntiTeamFlash] Эта команда доступна только игрокам.");
            return;
        }

        player.PrintToChat("=== ТЕСТ ПЛАГИНА ANTITEAMFLASH ===");
        player.PrintToChat("Плагин работает, текущие настройки:");
        player.PrintToChat($"  Enabled: {(Config.Enabled == 1 ? "Да" : "Нет")}");
        player.PrintToChat($"  FlashOwner: {(Config.FlashOwner == 1 ? "Да" : "Нет")}");
        player.PrintToChat($"  LogLevel: {Config.LogLevel}");
        player.PrintToChat($"  HudDuration: {Config.HudDuration:F1}");
        player.PrintToChat($"  AggregationTime: {Config.FlashAggregationTime:F1}");
        player.PrintToChat("При ослеплении врага или попытке ослепить своего вы увидите уведомления.");

        string testMsg = "<font color='green'>Тест AntiTeamFlash</font>";
        ShowHudMessage(player.Slot, testMsg, 2.0f);

        command.ReplyToCommand("[AntiTeamFlash] Тестовая информация выведена в чат и HUD.");
    }

    private void OnReloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        try
        {
            string configPath = Path.Combine(Server.GameDirectory, "counterstrikesharp", "configs", "plugins", "CS2AntiTeamFlash", "CS2AntiTeamFlash.json");
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                var newConfig = System.Text.Json.JsonSerializer.Deserialize<AntiTeamFlashConfig>(json);
                if (newConfig != null)
                {
                    OnConfigParsed(newConfig);
                    SaveConfig();
                }
            }
            else
            {
                SaveConfig();
            }

            foreach (var stats in _activeFlashes.Values)
                stats.CleanupTimer?.Kill();
            _activeFlashes.Clear();
            ClearHudMessages();

            command.ReplyToCommand("[AntiTeamFlash] Конфигурация перезагружена.");
            Log(LogLevel.Information, "Конфигурация перезагружена по команде.");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Ошибка при перезагрузке конфига: {ex.Message}");
            command.ReplyToCommand("[AntiTeamFlash] Ошибка при перезагрузке конфига.");
        }
    }

    private void OnSetEnabledCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand($"[AntiTeamFlash] Текущее значение enabled: {Config.Enabled}. Использование: css_antiteamflash_setenabled <0/1>");
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
            {
                ClearHudMessages();
            }
        }
        else
        {
            command.ReplyToCommand("[AntiTeamFlash] Неверное значение. Используйте 0 или 1.");
        }
    }

    private void OnSetFlashOwnerCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand($"[AntiTeamFlash] Текущее значение flashowner: {Config.FlashOwner}. Использование: css_antiteamflash_setflashowner <0/1>");
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
        {
            command.ReplyToCommand("[AntiTeamFlash] Неверное значение. Используйте 0 или 1.");
        }
    }

    private void OnSetLogLevelCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand($"[AntiTeamFlash] Текущий уровень логов: {Config.LogLevel} (0-Trace,1-Debug,2-Info,3-Warn,4-Error,5-Critical). Использование: css_antiteamflash_setloglevel <0-5>");
            return;
        }

        string arg = command.GetArg(1);
        if (int.TryParse(arg, out int value) && value >= 0 && value <= 5)
        {
            int old = Config.LogLevel;
            Config.LogLevel = value;
            SaveConfig();
            command.ReplyToCommand($"[AntiTeamFlash] Уровень логов изменён с {old} на {value}.");
        }
        else
        {
            command.ReplyToCommand("[AntiTeamFlash] Неверное значение. Используйте число от 0 до 5.");
        }
    }

    private void OnSetHudDurationCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand($"[AntiTeamFlash] Текущее значение hud_duration: {Config.HudDuration:F1}. Использование: css_antiteamflash_sethudduration <1.0-10.0>");
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
        {
            command.ReplyToCommand("[AntiTeamFlash] Неверное значение. Введите число с точкой (например 4.5).");
        }
    }

    private void OnSetAggregationTimeCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            command.ReplyToCommand($"[AntiTeamFlash] Текущее время агрегации: {Config.FlashAggregationTime:F1}. Использование: css_antiteamflash_setaggregationtime <1.0-10.0>");
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
        {
            command.ReplyToCommand("[AntiTeamFlash] Неверное значение. Введите число с точкой (например 3.0).");
        }
    }

    private void SaveConfig()
    {
        try
        {
            string configPath = Path.Combine(Server.GameDirectory, "counterstrikesharp", "configs", "plugins", "CS2AntiTeamFlash", "CS2AntiTeamFlash.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            string json = System.Text.Json.JsonSerializer.Serialize(Config, options);
            File.WriteAllText(configPath, json);
            Log(LogLevel.Debug, $"Конфигурация сохранена в {configPath}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[AntiTeamFlash] Ошибка сохранения конфигурации");
        }
    }

    public override void Unload(bool hotReload)
    {
        foreach (var stats in _activeFlashes.Values)
            stats.CleanupTimer?.Kill();
        _activeFlashes.Clear();
        ClearHudMessages();
        Log(LogLevel.Information, "Плагин выгружается.");
    }
}
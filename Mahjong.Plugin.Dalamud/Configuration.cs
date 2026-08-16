using System;
using Dalamud.Configuration;

namespace Mahjong.Plugin.Dalamud;

/// <summary><see cref="Version"/> is mutable per the Dalamud interface; only the migration runner writes it.</summary>
[Serializable]
public sealed record Configuration : IPluginConfiguration
{
    public const int CurrentSchemaVersion = 9;

    public int Version { get; set; } = CurrentSchemaVersion;

    public bool AutomationArmed { get; init; } = false;

    public bool SuggestionOnly { get; init; } = true;

    public bool TosAccepted { get; init; } = false;

    public bool DevMode { get; init; } = false;

    /// <summary>Legacy median delay retained for older configurations.</summary>
    public int HumanizedDelayMs { get; init; } = 3500;

    public bool HumanDelayEnabled { get; init; } = true;
    public int DiscardDelayMinMs { get; init; } = 3000;
    public int DiscardDelayMaxMs { get; init; } = 4000;
    public int TsumogiriDelayMinMs { get; init; } = 1200;
    public int TsumogiriDelayMaxMs { get; init; } = 2000;
    /// <summary>Legacy shared call delay retained for schema migration/backward compatibility.</summary>
    public int CallDelayMinMs { get; init; } = 2000;
    public int CallDelayMaxMs { get; init; } = 3500;
    public int PonDelayMinMs { get; init; } = 2000;
    public int PonDelayMaxMs { get; init; } = 3500;
    public int ChiDelayMinMs { get; init; } = 2000;
    public int ChiDelayMaxMs { get; init; } = 3500;
    public int MinKanDelayMinMs { get; init; } = 2000;
    public int MinKanDelayMaxMs { get; init; } = 3500;
    public int AnKanDelayMinMs { get; init; } = 2000;
    public int AnKanDelayMaxMs { get; init; } = 3500;
    public int ShouMinKanDelayMinMs { get; init; } = 2000;
    public int ShouMinKanDelayMaxMs { get; init; } = 3500;
    public int RiichiDelayMinMs { get; init; } = 3000;
    public int RiichiDelayMaxMs { get; init; } = 4000;
    public int WinDelayMinMs { get; init; } = 500;
    public int WinDelayMaxMs { get; init; } = 1200;
    public int PassDelayMinMs { get; init; } = 1000;
    public int PassDelayMaxMs { get; init; } = 2000;
    public int TurnTimeBudgetMs { get; init; } = 15000;
    public int EmergencyImmediateThresholdMs { get; init; } = 3000;

    public bool ShowInGameHighlight { get; init; } = true;

    public HighlightStyle HighlightStyle { get; init; } = HighlightStyle.NeonGlow;

    /// <summary>RGB color for hand-discard picks. Defaults to Theme.Accent.</summary>
    public RgbColor HighlightColorDiscard { get; init; } = RgbColor.Defaults.Discard;

    /// <summary>RGB color for tsumogiri (drawn-tile) picks. Defaults to Theme.Warn.</summary>
    public RgbColor HighlightColorTsumogiri { get; init; } = RgbColor.Defaults.Tsumogiri;

    /// <summary>RGB color for the hand tiles used by a recommended Chi/Pon/Kan.</summary>
    public RgbColor HighlightColorCall { get; init; } = RgbColor.Defaults.Call;

    /// <summary>Multiplier on overlay pulse alpha. 1.0 is default; 0.5 dims, 1.5 boosts.</summary>
    public float HighlightIntensity { get; init; } = 1.0f;

    public bool ShowSuggestionDetails { get; init; } = false;

    /// <summary>Sticky once the user accepts the first-arming auto-play warning.</summary>
    public bool AutoPlayConfirmed { get; init; } = false;

    /// <summary>Master switch for automatically accepting AI-selected calls.</summary>
    public bool AutoCallEnabled { get; init; } = false;

    public bool AutoPassEnabled { get; init; } = true;
    public bool AutoPonEnabled { get; init; } = true;
    public bool AutoChiEnabled { get; init; } = true;
    public bool AutoAnKanEnabled { get; init; } = true;
    public bool AutoMinKanEnabled { get; init; } = true;
    public bool AutoShouMinKanEnabled { get; init; } = true;

    /// <summary>Auto-clicks Next on the post-hand result modal so the next hand starts without manual input.</summary>
    public bool AutoAdvanceAfterHand { get; init; } = false;

    public bool EnableGameLogging { get; init; } = true;

    /// <summary>Write decision snapshots, Mortal inputs and decisions to /xllog.</summary>
    public bool DiagnosticDecisionLogging { get; init; } = true;

    /// <summary>Decision engine used for discard, calls, riichi and wins.</summary>
    public AiProvider AiProvider { get; init; } = AiProvider.BundledMortal;

    /// <summary>Akochan search preset. Realtime uses Akochan's official light preset.</summary>
    public AkochanInferenceProfile AkochanInferenceProfile { get; init; } = AkochanInferenceProfile.Realtime;

    /// <summary>Executable launched for a manually configured mjai JSONL engine.</summary>
    public string ExternalAiExecutable { get; init; } = string.Empty;

    /// <summary>Command-line arguments passed to the manually configured engine.</summary>
    public string ExternalAiArguments { get; init; } = string.Empty;

    /// <summary>Optional working directory for the manually configured engine.</summary>
    public string ExternalAiWorkingDirectory { get; init; } = string.Empty;

    /// <summary>Maximum wait for a normal AI response.</summary>
    public int ExternalAiTimeoutMs { get; init; } = 5000;

    /// <summary>Maximum wait while Mortal imports PyTorch and loads its model after process start.</summary>
    public int ExternalAiStartupTimeoutMs { get; init; } = 120000;

    /// <summary>Use the built-in heuristic when the external engine fails or returns an illegal action.</summary>
    public bool ExternalAiFallbackToBuiltIn { get; init; } = true;

    /// <summary>Use the optional remote inference endpoint supported by the bundled Mortal adapter.</summary>
    public bool MortalOnline { get; init; } = false;

    /// <summary>Remote Mortal inference endpoint. Local inference is used when disabled or unavailable.</summary>
    public string MortalServer { get; init; } = "http://server.akagiot.org/";

    /// <summary>Optional API key for the remote Mortal inference endpoint.</summary>
    public string MortalApiKey { get; init; } = string.Empty;

    /// <summary>Maximum CPU worker threads exposed to PyTorch/OpenMP.</summary>
    public int MortalCpuThreads { get; init; } = 4;

    /// <summary>Restart the Mortal process automatically after a crash or timeout.</summary>
    public bool MortalAutoRestart { get; init; } = true;

    /// <summary>
    /// Stable anonymous install identifier sent as <c>X-Install-Id</c>. <see cref="Guid.Empty"/>
    /// = not yet minted; the uploader treats that as a fatal init error.
    /// </summary>
    public Guid InstallId { get; init; } = Guid.Empty;
}

/// <summary>Visual treatment for the in-game best-tile overlay.</summary>
public enum HighlightStyle
{
    NeonGlow = 0,
    Arrow = 2,
}

/// <summary>Init-only property record (not positional) so Newtonsoft.Json round-trips it cleanly.</summary>
public sealed record RgbColor
{
    public float R { get; init; }
    public float G { get; init; }
    public float B { get; init; }

    public System.Numerics.Vector3 ToVector3() => new(R, G, B);

    public static RgbColor From(System.Numerics.Vector3 v) => new() { R = v.X, G = v.Y, B = v.Z };

    public static class Defaults
    {
        public static readonly RgbColor Discard = new() { R = 0.28f, G = 0.82f, B = 0.62f };
        public static readonly RgbColor Tsumogiri = new() { R = 0.98f, G = 0.80f, B = 0.30f };
        public static readonly RgbColor Call = new() { R = 0.40f, G = 0.68f, B = 0.98f };
    }
}

/// <summary>Available decision engines.</summary>
public enum AiProvider
{
    BuiltIn = 0,
    ExternalMjai = 1,
    BundledMortal = 2,
    BundledAkochan = 3,
}

/// <summary>Akochan search-depth preset.</summary>
public enum AkochanInferenceProfile
{
    Realtime = 0,
    Precision = 1,
}

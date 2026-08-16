using Dalamud.Plugin.Services;

namespace Mahjong.Plugin.Dalamud.ExternalAi;

/// <summary>選択中のAIだけを生成・実行する排他的な判断ポリシー。</summary>
internal sealed class SelectablePolicy : IPolicy, IDisposable
{
    internal const string PendingReasonPrefix = "Mortal pending";

    private readonly IPolicy builtIn;
    private readonly IConfigService<Configuration> config;
    private readonly IPluginLog log;
    private readonly string pluginAssemblyDirectory;
    private readonly object engineGate = new();

    private ExternalMjaiProcess? external;
    private ExternalMjaiProcess? akochan;
    private AiProvider? activeProvider;
    private bool disposed;

    public SelectablePolicy(
        IPolicy builtIn,
        IConfigService<Configuration> config,
        IPluginLog log,
        string pluginAssemblyDirectory)
    {
        this.builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.pluginAssemblyDirectory = pluginAssemblyDirectory ?? throw new ArgumentNullException(nameof(pluginAssemblyDirectory));
    }

    public string ExternalStatus => external?.Status ?? "停止中（未選択）";
    public long LastInferenceMs => external?.LastInferenceMs ?? 0;
    public int RestartCount => external?.RestartCount ?? 0;
    public string AkochanStatus => akochan?.Status ?? "停止中（未選択）";
    public long AkochanInferenceMs => akochan?.LastInferenceMs ?? 0;
    public int AkochanRestartCount => akochan?.RestartCount ?? 0;
    public string LastDecisionSource { get; private set; } = "なし";
    public string LastFallbackReason { get; private set; } = string.Empty;

    public bool TryGetAkochanChoice(StateSnapshot state, out ActionChoice choice)
    {
        lock (engineGate)
        {
            if (activeProvider != AiProvider.BundledAkochan || akochan is null)
            {
                choice = ActionChoice.Pass();
                return false;
            }
            return akochan.TryGetCachedChoice(state, out choice);
        }
    }

    /// <summary>Retains a dispatched open call in the selected engine until FFXIV confirms its follow-up discard.</summary>
    public void NotifyDispatchedOpenCall(ActionChoice choice, StateSnapshot state)
    {
        lock (engineGate)
        {
            if (disposed)
                return;

            switch (activeProvider)
            {
                case AiProvider.BundledAkochan:
                    akochan?.NotifyDispatchedOpenCall(choice, state);
                    break;
                case AiProvider.BundledMortal:
                case AiProvider.ExternalMjai:
                    external?.NotifyDispatchedOpenCall(choice, state);
                    break;
            }
        }
    }

    /// <summary>Drops an unconfirmed open-call dispatch from the selected engine.</summary>
    public void CancelDispatchedOpenCall(ActionChoice choice)
    {
        lock (engineGate)
        {
            if (disposed)
                return;

            switch (activeProvider)
            {
                case AiProvider.BundledAkochan:
                    akochan?.CancelDispatchedOpenCall(choice);
                    break;
                case AiProvider.BundledMortal:
                case AiProvider.ExternalMjai:
                    external?.CancelDispatchedOpenCall(choice);
                    break;
            }
        }
    }

    /// <summary>
    /// Notifies only the selected engine after the FFXIV state proves that an
    /// automatic action committed. This releases stale cached call choices and
    /// synchronizes the accepted meld; Akochan's mandatory discard is retained
    /// from the original [call, dahai] response by the autoplay loop.
    /// </summary>
    public void NotifyCommittedAction(ActionChoice choice, StateSnapshot state)
    {
        lock (engineGate)
        {
            if (disposed)
                return;

            switch (activeProvider)
            {
                case AiProvider.BundledAkochan:
                    akochan?.NotifyCommittedAction(choice, state);
                    break;
                case AiProvider.BundledMortal:
                case AiProvider.ExternalMjai:
                    external?.NotifyCommittedAction(choice, state);
                    break;
            }

            LastDecisionSource = "なし";
            LastFallbackReason = string.Empty;
        }
    }

    /// <summary>設定変更直後に未選択AIを停止し、選択AIだけを準備する。</summary>
    public void SelectProvider(AiProvider provider)
    {
        lock (engineGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeProvider == provider)
                return;

            external?.Dispose();
            external = null;
            akochan?.Dispose();
            akochan = null;

            switch (provider)
            {
                case AiProvider.BundledMortal:
                case AiProvider.ExternalMjai:
                    external = new ExternalMjaiProcess(log, pluginAssemblyDirectory);
                    break;
                case AiProvider.BundledAkochan:
                    akochan = new ExternalMjaiProcess(log, pluginAssemblyDirectory, ExternalEngineKind.AkochanComparison);
                    break;
                case AiProvider.BuiltIn:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
            }

            activeProvider = provider;
            LastDecisionSource = "なし";
            LastFallbackReason = string.Empty;
            log.Information("[AI切替] 選択AI={Provider}; 未選択AIは停止済み", provider);
        }
    }

    public void BeginPrewarm()
    {
        var cfg = config.Current;
        SelectProvider(cfg.AiProvider);
        if (cfg.AiProvider == AiProvider.BundledMortal)
            external?.BeginPrewarm(cfg);
    }

    /// <summary>
    /// Discards every cached/in-flight external-AI session and creates a fresh
    /// selected engine. The next snapshot is bootstrapped from the live board.
    /// Built-in AI has no external session and therefore only clears status.
    /// </summary>
    public void ForceResync(string reason)
    {
        lock (engineGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            AiProvider provider = config.Current.AiProvider;
            ActionChoice? committedCall = null;
            StateSnapshot? committedState = null;
            ExternalMjaiProcess? active = provider == AiProvider.BundledAkochan ? akochan : external;
            _ = active?.TryGetCommittedOwnCallRecovery(out committedCall, out committedState);

            external?.Dispose();
            external = null;
            akochan?.Dispose();
            akochan = null;

            switch (provider)
            {
                case AiProvider.BundledMortal:
                case AiProvider.ExternalMjai:
                    external = new ExternalMjaiProcess(log, pluginAssemblyDirectory);
                    break;
                case AiProvider.BundledAkochan:
                    akochan = new ExternalMjaiProcess(log, pluginAssemblyDirectory, ExternalEngineKind.AkochanComparison);
                    break;
                case AiProvider.BuiltIn:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
            }

            ExternalMjaiProcess? recreated = provider == AiProvider.BundledAkochan ? akochan : external;
            if (committedCall is not null && committedState is not null)
            {
                recreated?.NotifyCommittedAction(committedCall, committedState);
                log.Warning("[AI再同期] restored committed open-call recovery kind={Kind} hand={Hand} melds={Melds}",
                    committedCall.Kind, committedState.Hand.Count, committedState.OurMelds.Count);
            }

            activeProvider = provider;
            LastDecisionSource = "再同期中";
            LastFallbackReason = string.Empty;
            log.Warning("[AI再同期] selected engine recreated provider={Provider} reason={Reason}", provider, reason);

            if (provider == AiProvider.BundledMortal)
                external?.BeginPrewarm(config.Current);
        }
    }

    internal ActionChoice ChooseBuiltIn(StateSnapshot state) => builtIn.Choose(state);

    public static bool IsPendingChoice(ActionChoice choice) =>
        choice.Reasoning.StartsWith(PendingReasonPrefix, StringComparison.Ordinal)
        || choice.Reasoning.StartsWith("Akochan pending", StringComparison.Ordinal)
        || choice.Reasoning.StartsWith("Akochan unavailable", StringComparison.Ordinal)
        || choice.Reasoning.StartsWith("selected AI unavailable", StringComparison.Ordinal)
        || string.Equals(choice.Reasoning, "選択AI専用モード", StringComparison.Ordinal);

    public ActionChoice Choose(StateSnapshot state)
    {
        var cfg = config.Current;
        SelectProvider(cfg.AiProvider);

        if (cfg.AiProvider == AiProvider.BuiltIn)
        {
            LastDecisionSource = "内蔵AI";
            LastFallbackReason = string.Empty;
            return builtIn.Choose(state);
        }

        // 外部AI選択時は内蔵AIを一切実行しない。ActionChoice.Pass は
        // マッピング初期値としてのみ渡し、判断計算やフォールバックには使わない。
        static ActionChoice NoFallback() => ActionChoice.Pass("選択AI専用モード");

        if (cfg.AiProvider == AiProvider.BundledAkochan)
        {
            var selected = akochan ?? throw new InvalidOperationException("Akochanが初期化されていません。");
            if (selected.TryGetDeferredDecisionChoice(state, out var deferredDecisionChoice))
            {
                LastDecisionSource = "Akochan";
                LastFallbackReason = string.Empty;
                return deferredDecisionChoice;
            }
            if (selected.TryGetDeferredCallChoice(state, out var deferredCallChoice))
            {
                LastDecisionSource = "Akochan";
                LastFallbackReason = string.Empty;
                return deferredCallChoice;
            }

            if (selected.TryChoose(cfg, state, NoFallback, out var choice))
            {
                LastDecisionSource = "Akochan";
                LastFallbackReason = string.Empty;
                return choice with { Reasoning = string.IsNullOrWhiteSpace(choice.Reasoning) ? "Akochan" : choice.Reasoning };
            }

            if (selected.IsBusy || selected.Status.StartsWith("Akochan pending", StringComparison.Ordinal))
            {
                LastDecisionSource = "Akochan計算中";
                return ActionChoice.Pass(selected.Status.StartsWith("Akochan pending", StringComparison.Ordinal)
                    ? selected.Status
                    : $"Akochan pending: {selected.Status}");
            }

            LastDecisionSource = "Akochan利用不可";
            LastFallbackReason = selected.Status;
            return ActionChoice.Pass($"Akochan unavailable: {selected.Status}");
        }

        var externalSelected = external ?? throw new InvalidOperationException("外部AIが初期化されていません。");

        // Mortal may receive the correct MJAI call response a few milliseconds before
        // EMJ finishes replacing a transient Pon/Pass surface with the authoritative
        // Chi/Pass surface (or vice versa). The native engine has already consumed the
        // discard event, so issuing another request produces an empty batch and strands
        // the prompt. Consume the response retained for this unchanged board position
        // before attempting to queue any new request.
        if (externalSelected.TryGetDeferredDecisionChoice(state, out var deferredExternalChoice))
        {
            LastDecisionSource = cfg.AiProvider == AiProvider.BundledMortal ? "Mortal" : "外部MJAI";
            LastFallbackReason = string.Empty;
            return deferredExternalChoice with
            {
                Reasoning = string.IsNullOrWhiteSpace(deferredExternalChoice.Reasoning)
                    ? LastDecisionSource
                    : deferredExternalChoice.Reasoning,
            };
        }

        // Same lifecycle as Akochan DeferredCall: 298k often answers before the
        // Chi/Pon/Pass buttons exist. Keep that answer and publish it only when
        // the live call surface matches the retained offer key.
        if (externalSelected.TryGetDeferredCallChoice(state, out var deferredExternalCallChoice))
        {
            LastDecisionSource = cfg.AiProvider == AiProvider.BundledMortal ? "Mortal" : "外部MJAI";
            LastFallbackReason = string.Empty;
            return deferredExternalCallChoice with
            {
                Reasoning = string.IsNullOrWhiteSpace(deferredExternalCallChoice.Reasoning)
                    ? LastDecisionSource
                    : deferredExternalCallChoice.Reasoning,
            };
        }

        if (externalSelected.TryChoose(cfg, state, NoFallback, out var externalChoice))
        {
            LastDecisionSource = cfg.AiProvider == AiProvider.BundledMortal ? "Mortal" : "外部MJAI";
            LastFallbackReason = string.Empty;
            return externalChoice with
            {
                Reasoning = string.IsNullOrWhiteSpace(externalChoice.Reasoning)
                    ? LastDecisionSource
                    : externalChoice.Reasoning,
            };
        }

        if (externalSelected.IsBusy || ExternalMjaiProcess.IsPendingStatus(externalSelected.Status))
        {
            string pendingLabel = cfg.AiProvider == AiProvider.BundledMortal ? "Mortal計算中" : "外部MJAI計算中";
            string pendingReason = ExternalMjaiProcess.IsPendingStatus(externalSelected.Status)
                ? externalSelected.Status
                : $"{PendingReasonPrefix}: {externalSelected.Status}";

            if (externalSelected.TryGetCachedChoice(state, out var pendingCached))
            {
                LastDecisionSource = pendingLabel;
                LastFallbackReason = string.Empty;
                return pendingCached with { Reasoning = pendingReason };
            }

            if (externalSelected.TryGetPendingRetainedChoice(state, out var retainedChoice))
            {
                LastDecisionSource = pendingLabel;
                LastFallbackReason = string.Empty;
                return retainedChoice with { Reasoning = pendingReason };
            }

            LastDecisionSource = pendingLabel;
            return ActionChoice.Pass(pendingReason);
        }

        LastDecisionSource = "選択AI利用不可";
        LastFallbackReason = externalSelected.Status;
        return ActionChoice.Pass($"selected AI unavailable: {externalSelected.Status}");
    }

    public void Dispose()
    {
        lock (engineGate)
        {
            if (disposed)
                return;
            disposed = true;
            external?.Dispose();
            external = null;
            akochan?.Dispose();
            akochan = null;
            activeProvider = null;
        }
    }
}

using System.Diagnostics;
using System.Text;
using Dalamud.Plugin.Services;

namespace Mahjong.Plugin.Dalamud.ExternalAi;

/// <summary>
/// Downloads Python, PyTorch, and the public Mortal model into LocalAppData
/// the first time the plugin needs them. The Dalamud zip stays small; users
/// only register the custom repository.
/// </summary>
internal sealed class MortalRuntimeInstaller : IDisposable
{
    public enum RuntimeInstallState
    {
        Ready,
        Missing,
        Installing,
        Failed,
    }

    private readonly IPluginLog log;
    private readonly IChatGui chat;
    private readonly IFramework framework;
    private readonly string pluginDirectory;
    private readonly object gate = new();
    private Process? process;
    private bool disposed;

    public RuntimeInstallState State { get; private set; } = RuntimeInstallState.Missing;
    public string StatusText { get; private set; } = string.Empty;
    public event Action? Completed;

    public MortalRuntimeInstaller(
        IPluginLog log,
        IChatGui chat,
        IFramework framework,
        string pluginDirectory)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.chat = chat ?? throw new ArgumentNullException(nameof(chat));
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
        this.pluginDirectory = pluginDirectory ?? throw new ArgumentNullException(nameof(pluginDirectory));
        RefreshReadyState();
    }

    public void StartIfNeeded()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (State is RuntimeInstallState.Ready or RuntimeInstallState.Installing)
                return;

            if (MortalRuntimeLocator.IsInstalled(pluginDirectory))
            {
                MarkReady("Mortalランタイムは利用可能です。");
                return;
            }

            string script = Path.Combine(pluginDirectory, "external-ai", "Install-MortalRuntime.ps1");
            if (!File.Exists(script))
            {
                State = RuntimeInstallState.Failed;
                StatusText = "インストーラーが見つかりません。プラグインを再インストールしてください。";
                log.Error("[MortalSetup] installer script missing: {Path}", script);
                return;
            }

            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -SkipSmokeTest",
                WorkingDirectory = pluginDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            Process child;
            try
            {
                child = new Process { StartInfo = start, EnableRaisingEvents = true };
                child.OutputDataReceived += (_, e) => OnInstallerLine(e.Data);
                child.ErrorDataReceived += (_, e) => OnInstallerLine(e.Data);
                child.Exited += (_, _) => OnInstallerExited(child);
                if (!child.Start())
                    throw new InvalidOperationException("Failed to start PowerShell.");
                child.BeginOutputReadLine();
                child.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                State = RuntimeInstallState.Failed;
                StatusText = "Mortalセットアップを開始できませんでした: " + ex.Message;
                log.Error(ex, "[MortalSetup] failed to start installer");
                return;
            }

            process = child;
            State = RuntimeInstallState.Installing;
            StatusText = "Mortal AI を初回セットアップしています。Python / PyTorch / モデルをダウンロードします。";
            log.Information("[MortalSetup] started pid={Pid} script={Script}", child.Id, script);
            NotifyChat("Mortal AI をセットアップしています。初回は数分かかることがあります。");
        }
    }

    public void Dispose()
    {
        Process? child;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            child = process;
            process = null;
        }

        if (child is null)
            return;

        try
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[MortalSetup] could not stop installer process");
        }
        finally
        {
            child.Dispose();
        }
    }

    private void RefreshReadyState()
    {
        if (MortalRuntimeLocator.IsInstalled(pluginDirectory))
        {
            MarkReady("Mortalランタイムは利用可能です。");
            return;
        }

        State = RuntimeInstallState.Missing;
        StatusText = "Mortalランタイムは未インストールです。初回セットアップで自動導入します。";
    }

    private void MarkReady(string text)
    {
        State = RuntimeInstallState.Ready;
        StatusText = text;
    }

    private void OnInstallerLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        string trimmed = line.Trim();
        lock (gate)
        {
            if (State == RuntimeInstallState.Installing)
                StatusText = trimmed.Length > 160 ? trimmed[..160] + "…" : trimmed;
        }

        log.Information("[MortalSetup] {Line}", trimmed);
    }

    private void OnInstallerExited(Process child)
    {
        int exitCode;
        try
        {
            exitCode = child.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        bool ready;
        lock (gate)
        {
            if (ReferenceEquals(process, child))
                process = null;
            child.Dispose();

            ready = exitCode == 0 && MortalRuntimeLocator.IsInstalled(pluginDirectory);
            if (ready)
            {
                MarkReady("Mortalランタイムのセットアップが完了しました。");
            }
            else
            {
                State = RuntimeInstallState.Failed;
                StatusText = "Mortalセットアップに失敗しました。設定画面から再試行してください。終了コード=" + exitCode;
            }
        }

        log.Information("[MortalSetup] finished exit={Exit} ready={Ready}", exitCode, ready);
        framework.RunOnFrameworkThread(() =>
        {
            if (ready)
            {
                NotifyChat("Mortal AI のセットアップが完了しました。");
                Completed?.Invoke();
            }
            else
            {
                NotifyChat("Mortal AI のセットアップに失敗しました。設定画面から再試行できます。");
            }
        });
    }

    private void NotifyChat(string message)
    {
        try
        {
            chat.Print(message);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[MortalSetup] could not print chat status");
        }
    }
}

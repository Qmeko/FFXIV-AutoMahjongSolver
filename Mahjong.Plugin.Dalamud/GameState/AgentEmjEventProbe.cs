using System;
using System.IO;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>
/// Records the actual AtkValue events delivered to AgentEmj's inherited
/// AtkEventInterface. The two functions are virtual slots 0 and 1 as defined
/// by FFXIVClientStructs.
/// </summary>
public readonly record struct AgentEmjObservedEvent(
    DateTime ObservedAtUtc,
    int Opcode,
    int? Argument,
    int? Argument2,
    int? Argument3,
    int ValueCount);

internal sealed unsafe class AgentEmjEventProbe : IDisposable
{
    private unsafe delegate AtkValue* ReceiveEventDelegate(
        AgentInterface* agent,
        AtkValue* returnValue,
        AtkValue* values,
        uint valueCount,
        ulong eventKind);

    private const int MaxValues = 2048;
    private readonly IFramework framework;
    private readonly IGameInteropProvider gameInterop;
    private readonly IPluginLog log;
    private readonly string logPath;
    private readonly object installGate = new();
    private Hook<ReceiveEventDelegate>? receiveEventHook;
    private Hook<ReceiveEventDelegate>? receiveEventWithResultHook;
    private bool installed;
    private bool disposed;

    internal event Action<AgentEmjObservedEvent>? EventObserved;

    public AgentEmjEventProbe(
        IFramework framework,
        IGameInteropProvider gameInterop,
        IPluginLog log,
        string pluginConfigDir)
    {
        this.framework = framework;
        this.gameInterop = gameInterop;
        this.log = log;
        Directory.CreateDirectory(pluginConfigDir);
        logPath = Path.Combine(pluginConfigDir, "agent-emj-events.log");
        framework.Update += OnFrameworkUpdate;
        TryInstall();
    }

    private void OnFrameworkUpdate(IFramework _) => TryInstall();

    private void TryInstall()
    {
        lock (installGate)
        {
            if (installed || disposed)
                return;

            try
            {
                var module = AgentModule.Instance();
                var agent = module == null
                    ? null
                    : module->GetAgentByInternalId(AgentId.Emj);
                if (agent == null)
                    return;

                nint vtable = *(nint*)agent;
                if (vtable == 0)
                    return;
                nint receiveAddress = *(nint*)vtable;
                nint withResultAddress = *((nint*)vtable + 1);
                if (receiveAddress == 0 || withResultAddress == 0)
                    return;

                receiveEventHook = gameInterop.HookFromAddress<ReceiveEventDelegate>(
                    receiveAddress, ReceiveEventDetour);
                receiveEventHook.Enable();

                if (withResultAddress != receiveAddress)
                {
                    receiveEventWithResultHook = gameInterop.HookFromAddress<ReceiveEventDelegate>(
                        withResultAddress, ReceiveEventWithResultDetour);
                    receiveEventWithResultHook.Enable();
                }

                installed = true;
                framework.Update -= OnFrameworkUpdate;
                log.Information(
                    $"[AgentEmjEvent] hooks active agent=0x{(nint)agent:X} " +
                    $"receive=0x{receiveAddress:X} withResult=0x{withResultAddress:X}");
            }
            catch (Exception ex)
            {
                // Never leave a partially enabled hook behind for the next
                // framework-update retry to hook a second time.
                ReleaseHooks();
                log.Warning($"[AgentEmjEvent] hook install deferred: {ex.Message}");
            }
        }
    }

    private AtkValue* ReceiveEventDetour(
        AgentInterface* agent,
        AtkValue* returnValue,
        AtkValue* values,
        uint valueCount,
        ulong eventKind)
    {
        AgentEmjObservedEvent? observed = Capture(values, valueCount);
        Record("ReceiveEvent", agent, values, valueCount, eventKind);
        AtkValue* result = receiveEventHook!.Original(
            agent, returnValue, values, valueCount, eventKind);
        Publish(observed);
        return result;
    }

    private AtkValue* ReceiveEventWithResultDetour(
        AgentInterface* agent,
        AtkValue* returnValue,
        AtkValue* values,
        uint valueCount,
        ulong eventKind)
    {
        AgentEmjObservedEvent? observed = Capture(values, valueCount);
        Record("ReceiveEventWithResult", agent, values, valueCount, eventKind);
        AtkValue* result = receiveEventWithResultHook!.Original(
            agent, returnValue, values, valueCount, eventKind);
        Publish(observed);
        return result;
    }


    private static AgentEmjObservedEvent? Capture(AtkValue* values, uint valueCount)
    {
        if (values == null || valueCount == 0 || values[0].Type != AtkValueType.Int)
            return null;
        int? argument = valueCount > 1 && values[1].Type == AtkValueType.Int
            ? values[1].Int
            : null;
        int? argument2 = valueCount > 2 && values[2].Type == AtkValueType.Int ? values[2].Int : null;
        int? argument3 = valueCount > 3 && values[3].Type == AtkValueType.Int ? values[3].Int : null;
        return new AgentEmjObservedEvent(
            DateTime.UtcNow, values[0].Int, argument, argument2, argument3, (int)valueCount);
    }

    private void Publish(AgentEmjObservedEvent? observed)
    {
        if (observed is not { } evt || EventObserved is not { } observers)
            return;
        try
        {
            observers(evt);
        }
        catch (Exception ex)
        {
            log.Warning($"[AgentEmjEvent] observer failed: {ex.Message}");
        }
    }

    private void Record(
        string source,
        AgentInterface* agent,
        AtkValue* values,
        uint valueCount,
        ulong eventKind)
    {
        try
        {
            int count = (int)Math.Min(valueCount, MaxValues);
            var sb = new StringBuilder(1024);
            sb.Append(DateTime.UtcNow.ToString("O"))
                .Append(" source=").Append(source)
                .Append(" agent=0x").Append(((nint)agent).ToString("X"))
                .Append(" eventKind=").Append(eventKind)
                .Append(" valueCount=").Append(valueCount)
                .Append(" values=[");
            for (int i = 0; i < count; i++)
            {
                if (i != 0)
                    sb.Append(',');
                AppendValue(sb, i, values == null ? default : values[i]);
            }
            if (valueCount > MaxValues)
                sb.Append(",...");
            sb.Append(']');
            string line = sb.ToString();
            File.AppendAllText(logPath, line + Environment.NewLine);
            log.Information($"[AgentEmjEvent] {line}");
        }
        catch (Exception ex)
        {
            log.Warning($"[AgentEmjEvent] record failed: {ex.Message}");
        }
    }

    private static void AppendValue(StringBuilder sb, int index, AtkValue value)
    {
        sb.Append(index).Append(':').Append(value.Type).Append('=');
        switch (value.Type)
        {
            case AtkValueType.Int:
                sb.Append(value.Int);
                break;
            case AtkValueType.UInt:
                sb.Append(value.UInt);
                break;
            case AtkValueType.Bool:
                sb.Append(value.Byte != 0 ? "true" : "false");
                break;
            case AtkValueType.Float:
                sb.Append(value.Float);
                break;
            case AtkValueType.String:
            case AtkValueType.ConstString:
            case AtkValueType.ManagedString:
                sb.Append("ptr:0x").Append(((nint)value.String.Value).ToString("X"));
                break;
            default:
                sb.Append("raw:0x").Append(value.UInt.ToString("X"));
                break;
        }
    }

    public void Dispose()
    {
        lock (installGate)
        {
            if (disposed)
                return;
            disposed = true;
            framework.Update -= OnFrameworkUpdate;
            ReleaseHooks();
            installed = false;
        }
    }

    private void ReleaseHooks()
    {
        receiveEventHook?.Disable();
        receiveEventWithResultHook?.Disable();
        receiveEventHook?.Dispose();
        receiveEventWithResultHook?.Dispose();
        receiveEventHook = null;
        receiveEventWithResultHook = null;
    }
}

// GlamourerIpc.cs
using Dalamud.Plugin;
using Dalamud.Game.ClientState.Objects.Types;
using Glamourer.Api.IpcSubscribers;
using Glamourer.Api.Enums;
using System;
using System.Collections.Concurrent;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Snappy.Utils;

namespace Snappy.Managers.Glamourer;

public partial class GlamourerIpc : IDisposable
{
    private readonly DalamudUtil _dalamudUtil;
    private readonly ConcurrentQueue<Action> _queue;
    private readonly ApplyState _apply;
    private readonly RevertState _revert;
    private readonly GetStateBase64 _get;
    private readonly ApiVersion _version;
    private readonly string _backupBase64 = ""; // truncate safely
    private Func<ICharacter, string?>? _getBase64FromCharacter;
    private readonly IDalamudPluginInterface _pluginInterface;

    public GlamourerIpc(IDalamudPluginInterface pi, DalamudUtil dalamudUtil, ConcurrentQueue<Action> queue)
    {
        _dalamudUtil = dalamudUtil;
        _pluginInterface = pi;
        _queue = queue;
        _version = new ApiVersion(pi);
        _get = new GetStateBase64(pi);
        _apply = new ApplyState(pi);
        _revert = new RevertState(pi);

        // Defer IPC hook via Framework.Update
        _dalamudUtil.FrameworkUpdate += WaitForGlamourer;
    }

    private void WaitForGlamourer()
    {
        try
        {
            if (_getBase64FromCharacter == null)
            {
                _getBase64FromCharacter = _pluginInterface
                    .GetIpcSubscriber<ICharacter, string?>("Glamourer.GetStateBase64FromCharacter")
                    .InvokeFunc!;
                Logger.Info("Glamourer IPC hooked!");
            }
        }
        catch
        {
            // Try again next frame
            return;
        }

        // IPC is now available, stop checking
        _dalamudUtil.FrameworkUpdate -= WaitForGlamourer;
    }

    public void Dispose() { }

    public void ApplyState(string? base64, ICharacter obj)
    {
        if (!Check() || string.IsNullOrEmpty(base64)) return;
        Logger.Verbose("Glamourer applying for " + obj.Address.ToString("X"));
        _apply.Invoke(base64, obj.ObjectIndex);
    }

    public void RevertState(IGameObject obj)
    {
        if (!Check()) return;
        if (obj is ICharacter c)
        {
            _revert.Invoke(c.ObjectIndex);
            Logger.Info($"Glamourer reverting state for {c.Name} ({c.Address:X})");
        }
        else
        {
            Logger.Error("Tried to revert non-character with Glamourer");
        }
    }

    public string GetCharacterCustomization(IntPtr ptr)
    {
        if (!Check()) return _backupBase64;

        try
        {
            var gameObj = _dalamudUtil.CreateGameObject(ptr);
            if (gameObj is ICharacter c)
            {
                Logger.Debug($"Getting customization for {c.Name} / {c.ObjectIndex}");
                (GlamourerApiEc ec, string result) = _get.Invoke(c.ObjectIndex);
                if (!string.IsNullOrEmpty(result)) return result;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Glamourer IPC error: " + ex.Message);
        }

        Logger.Warn("Falling back to stored base64");
        return SafeBase64(_backupBase64);
    }

    private bool Check()
    {
        try
        {
            return _version.Invoke() is { Major: 1, Minor: >= 1 };
        }
        catch
        {
            Logger.Warn("Glamourer not available");
            return false;
        }
    }

    private string SafeBase64(string input)
    {
        try
        {
            var bytes = Convert.FromBase64String(input);
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return _backupBase64;
        }
    }

    public string? GetClipboardGlamourerString(ICharacter character)
    {
        if (!Check() || _getBase64FromCharacter == null)
        {
            Logger.Warn("[GlamourerIpc] Clipboard IPC not available.");
            return SafeBase64(_backupBase64);
        }

        try
        {
            var result = _getBase64FromCharacter(character);
            if (!string.IsNullOrEmpty(result))
                return result;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[GlamourerIpc] Failed to get clipboard-style Glamourer string: {ex.Message}");
        }

        Logger.Warn("[GlamourerIpc] Glamourer string was null or empty, returning fallback.");
        return SafeBase64(_backupBase64);
    }

}
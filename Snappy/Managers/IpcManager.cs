using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using ECommons.Reflection;
using Snappy.Managers.Customize;
using Snappy.Managers.Glamourer;
using Snappy.Managers.Penumbra;
using Snappy.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Action = System.Action;

namespace Snappy.Managers;

public delegate void PenumbraRedrawEvent(IntPtr address, int objTblIdx);
public delegate void HeelsOffsetChange(float change);
public delegate void PenumbraResourceLoadEvent(IntPtr drawObject, string gamePath, string filePath);
public delegate void CustomizePlusScaleChange(string? scale);
public delegate void GPoseChange(bool inGPose);

public class IpcManager : IDisposable
{
    private readonly DalamudUtil _dalamudUtil;
    private readonly ConcurrentQueue<Action> actionQueue = new();

    private readonly PenumbraIpc _penumbra;
    private readonly GlamourerIpc _glamourer;
    private readonly CustomizeIpc _customize;

    public event GPoseChange? GPoseChanged;

    public IpcManager(IDalamudPluginInterface pi, DalamudUtil dalamudUtil)
    {
        Logger.Verbose("Creating IpcManager delegator");

        _dalamudUtil = dalamudUtil;
        _penumbra = new PenumbraIpc(pi, dalamudUtil, actionQueue);
        _glamourer = new GlamourerIpc(pi, dalamudUtil, actionQueue);
        _customize = new CustomizeIpc(pi, dalamudUtil);

        _glamourer.GPoseChanged += OnGPoseChanged;
        _dalamudUtil.FrameworkUpdate += HandleActionQueue;
        _dalamudUtil.ZoneSwitchEnd += () => actionQueue.Clear();
    }

    private void OnGPoseChanged(bool inGPose) => GPoseChanged?.Invoke(inGPose);

    private void HandleActionQueue()
    {
        if (actionQueue.TryDequeue(out var action) && action != null)
        {
            Logger.Debug("Execution action in queue: " + action.Method);
            action();
        }
    }

    public void Dispose()
    {
        _penumbra.Dispose();
        _glamourer.GPoseChanged -= OnGPoseChanged;
        _glamourer.Dispose();
        _customize.Dispose();
        _dalamudUtil.FrameworkUpdate -= HandleActionQueue;
        actionQueue.Clear();
    }

    // Penumbra passthroughs
    public void PenumbraRemoveTemporaryCollection(string name) => _penumbra.RemoveTemporaryCollection(name);
    public void PenumbraRedraw(int objIdx) => _penumbra.Redraw(objIdx);
    public void PenumbraRedraw(IntPtr objPtr) => _penumbra.Redraw(objPtr);
    public string GetMetaManipulations(int objIdx) => _penumbra.GetMetaManipulations(objIdx);
    public void PenumbraSetTempMods(ICharacter character, int? idx, Dictionary<string, string> mods, string manips) => _penumbra.SetTemporaryMods(character, idx, mods, manips);
    // Passthroughs for Penumbra path helpers
    public string PenumbraResolvePath(string path) => _penumbra.ResolvePath(path);
    public string PenumbraResolvePathObject(string path, int objIdx) => _penumbra.ResolvePathObject(path, objIdx);
    public string[] PenumbraReverseResolveObject(string path, int objIdx) => _penumbra.ReverseResolveObject(path, objIdx);
    public string[] PenumbraReverseResolvePlayer(string path) => _penumbra.ReverseResolvePlayer(path);


    // Glamourer passthroughs
    public GlamourerIpc GlamourerIpc => _glamourer;
    public string GetGlamourerState(ICharacter c) => _glamourer.GetCharacterCustomization(c.Address);
    public string GetGlamourerStateFromMare(ICharacter character)
    {
        string glamourerData = string.Empty;
        Logger.Debug($"Attempting to get Glamourer state from Mare for {character.Name.TextValue}");
        if (!DalamudReflector.TryGetDalamudPlugin("MareSynchronos", out var marePlugin, true))
        {
            Logger.Warn("Mare Synchronos plugin not found or not loaded. Cannot reflect for Glamourer data.");
            return string.Empty;
        }

        try
        {
            Logger.Debug("Reflection Step 1: Successfully got Mare Synchronos plugin instance.");
            var host = marePlugin.GetFoP("_host");
            if (host == null)
            {
                Logger.Warn("Reflection failed: Could not find _host in Mare Synchronos plugin.");
                return string.Empty;
            }
            Logger.Debug("Reflection Step 2: Successfully got _host instance from Mare.");

            var serviceProvider = host.GetFoP("Services") as IServiceProvider;
            if (serviceProvider == null)
            {
                Logger.Warn("Reflection failed: Could not find Services IServiceProvider in _host.");
                return string.Empty;
            }
            Logger.Debug("Reflection Step 3: Successfully got IServiceProvider from _host.");

            var pairManagerType = marePlugin.GetType().Assembly.GetType("MareSynchronos.PlayerData.Pairs.PairManager");
            if (pairManagerType == null)
            {
                Logger.Warn("Reflection failed: Could not find type MareSynchronos.PlayerData.Pairs.PairManager.");
                return string.Empty;
            }
            Logger.Debug("Reflection Step 4: Successfully got PairManager type.");

            var pairManager = serviceProvider.GetService(pairManagerType);
            if (pairManager == null)
            {
                Logger.Warn("Reflection failed: Could not get PairManager service from IServiceProvider.");
                return string.Empty;
            }
            Logger.Debug("Reflection Step 5: Successfully got PairManager service instance.");

            var allClientPairs = pairManager.GetFoP("_allClientPairs") as System.Collections.IDictionary;
            if (allClientPairs == null)
            {
                Logger.Warn("Reflection failed: Could not find _allClientPairs in PairManager.");
                return string.Empty;
            }
            Logger.Debug($"Reflection Step 6: Successfully found _allClientPairs dictionary with {allClientPairs.Count} entries.");

            foreach (var pairObject in allClientPairs.Values)
            {
                var pairPlayerName = pairObject.GetFoP("PlayerName") as string;
                if (string.Equals(pairPlayerName, character.Name.TextValue, StringComparison.Ordinal))
                {
                    Logger.Debug($"Found matching pair for character {character.Name.TextValue}. Checking for Glamourer data.");

                    var lastReceivedCharacterData = pairObject.GetFoP("LastReceivedCharacterData");
                    if (lastReceivedCharacterData == null)
                    {
                        Logger.Debug($"LastReceivedCharacterData for {character.Name.TextValue} is null for this pair. This is normal if the user hasn't sent data yet. Continuing search...");
                        continue;
                    }
                    Logger.Debug("Reflection Step 7: Successfully got LastReceivedCharacterData.");

                    var glamourerDataDict = lastReceivedCharacterData.GetFoP("GlamourerData") as System.Collections.IDictionary;
                    if (glamourerDataDict == null)
                    {
                        Logger.Warn("Reflection failed: Could not find GlamourerData dictionary in CharacterData.");
                        continue;
                    }
                    Logger.Debug($"Reflection Step 8: Successfully found GlamourerData dictionary with {glamourerDataDict.Count} entries.");

                    if (glamourerDataDict.Count == 0)
                    {
                        Logger.Debug("GlamourerData dictionary is empty for this pair.");
                        continue;
                    }

                    var objectKindEnum = glamourerDataDict.Keys.Cast<object>().First().GetType();
                    if (!objectKindEnum.IsEnum)
                    {
                        Logger.Warn($"Reflection failed: Reflected key type '{objectKindEnum.FullName}' is not an enum.");
                        return string.Empty; // Fail fast if type is wrong
                    }
                    Logger.Debug($"Reflection Step 9: Successfully determined ObjectKind enum type: {objectKindEnum.FullName}");

                    var playerObjectKind = Enum.ToObject(objectKindEnum, 0); // ObjectKind.Player
                    Logger.Debug($"Searching for ObjectKind.Player key ({playerObjectKind}) in GlamourerData dictionary.");

                    if (glamourerDataDict.Contains(playerObjectKind))
                    {
                        var glamourerDataJson = glamourerDataDict[playerObjectKind] as string;
                        if (!string.IsNullOrEmpty(glamourerDataJson))
                        {
                            Logger.Info($"SUCCESS: Retrieved Glamourer data from Mare for {character.Name.TextValue}.");
                            glamourerData = glamourerDataJson;
                            break;
                        }
                        else
                        {
                            Logger.Debug("Glamourer data for Player object was present but null or empty.");
                        }
                    }
                    else
                    {
                        Logger.Debug("GlamourerData dictionary does not contain an entry for the Player object kind.");
                    }
                }
            }

            if (string.IsNullOrEmpty(glamourerData))
            {
                Logger.Debug($"No valid Glamourer data found in any matching Mare pairs for {character.Name.TextValue}.");
            }
        }
        catch (Exception e)
        {
            Logger.Error("An exception occurred while reflecting into Mare Synchronos.", e);
        }

        return glamourerData;
    }
    public void ApplyGlamourerState(string? base64, ICharacter c) => _glamourer.ApplyState(base64, c);
    public void RevertGlamourerState(IGameObject c) => _glamourer.RevertState(c);

    // CustomizePlus passthroughs
    public bool IsCustomizePlusAvailable() => _customize.CheckApi();
    public string GetCustomizePlusScale(ICharacter c) => _customize.GetScaleFromCharacter(c);
    public string GetCustomizePlusScaleFromMare(ICharacter character)
    {
        string cPlusData = string.Empty;
        Logger.Debug($"Attempting to get C+ scale from Mare for {character.Name.TextValue}");
        if (!DalamudReflector.TryGetDalamudPlugin("MareSynchronos", out var marePlugin, true))
        {
            Logger.Warn("Mare Synchronos plugin not found or not loaded. Cannot reflect for C+ data.");
            return string.Empty;
        }

        try
        {
            Logger.Debug("Reflection Step 1: Successfully got Mare Synchronos plugin instance.");
            var host = marePlugin.GetFoP("_host");
            if (host == null)
            {
                Logger.Warn("Reflection failed: Could not find _host in Mare Synchronos plugin.");
                return string.Empty;
            }
            Logger.Debug("Reflection Step 2: Successfully got _host instance from Mare.");

            var serviceProvider = host.GetFoP("Services") as IServiceProvider;
            if (serviceProvider == null)
            {
                Logger.Warn("Reflection failed: Could not find Services IServiceProvider in _host.");
                return string.Empty;
            }
            Logger.Debug("Reflection Step 3: Successfully got IServiceProvider from _host.");

            var pairManagerType = marePlugin.GetType().Assembly.GetType("MareSynchronos.PlayerData.Pairs.PairManager");
            if (pairManagerType == null)
            {
                Logger.Warn("Reflection failed: Could not find type MareSynchronos.PlayerData.Pairs.PairManager.");
                return string.Empty;
            }
            Logger.Debug("Reflection Step 4: Successfully got PairManager type.");

            var pairManager = serviceProvider.GetService(pairManagerType);
            if (pairManager == null)
            {
                Logger.Warn("Reflection failed: Could not get PairManager service from IServiceProvider.");
                return string.Empty;
            }
            Logger.Debug("Reflection Step 5: Successfully got PairManager service instance.");

            var allClientPairs = pairManager.GetFoP("_allClientPairs") as System.Collections.IDictionary;
            if (allClientPairs == null)
            {
                Logger.Warn("Reflection failed: Could not find _allClientPairs in PairManager.");
                return string.Empty;
            }
            Logger.Debug($"Reflection Step 6: Successfully found _allClientPairs dictionary with {allClientPairs.Count} entries.");

            foreach (var pairObject in allClientPairs.Values)
            {
                var pairPlayerName = pairObject.GetFoP("PlayerName") as string;
                if (string.Equals(pairPlayerName, character.Name.TextValue, StringComparison.Ordinal))
                {
                    Logger.Debug($"Found matching pair for character {character.Name.TextValue}. Checking for C+ data.");

                    var lastReceivedCharacterData = pairObject.GetFoP("LastReceivedCharacterData");
                    if (lastReceivedCharacterData == null)
                    {
                        Logger.Debug($"LastReceivedCharacterData for {character.Name.TextValue} is null for this pair. This is normal if the user hasn't sent data yet. Continuing search...");
                        continue;
                    }
                    Logger.Debug("Reflection Step 7: Successfully got LastReceivedCharacterData.");

                    var customizePlusDataDict = lastReceivedCharacterData.GetFoP("CustomizePlusData") as System.Collections.IDictionary;
                    if (customizePlusDataDict == null)
                    {
                        Logger.Warn("Reflection failed: Could not find CustomizePlusData dictionary in CharacterData.");
                        continue;
                    }
                    Logger.Debug($"Reflection Step 8: Successfully found CustomizePlusData dictionary with {customizePlusDataDict.Count} entries.");

                    if (customizePlusDataDict.Count == 0)
                    {
                        Logger.Debug("CustomizePlusData dictionary is empty for this pair.");
                        continue;
                    }

                    var objectKindEnum = customizePlusDataDict.Keys.Cast<object>().First().GetType();
                    if (!objectKindEnum.IsEnum)
                    {
                        Logger.Warn($"Reflection failed: Reflected key type '{objectKindEnum.FullName}' is not an enum.");
                        return string.Empty; // Fail fast if type is wrong
                    }
                    Logger.Debug($"Reflection Step 9: Successfully determined ObjectKind enum type: {objectKindEnum.FullName}");

                    var playerObjectKind = Enum.ToObject(objectKindEnum, 0); // ObjectKind.Player
                    Logger.Debug($"Searching for ObjectKind.Player key ({playerObjectKind}) in CustomizePlusData dictionary.");

                    if (customizePlusDataDict.Contains(playerObjectKind))
                    {
                        var customizePlusDataJson = customizePlusDataDict[playerObjectKind] as string;
                        if (!string.IsNullOrEmpty(customizePlusDataJson))
                        {
                            Logger.Info($"SUCCESS: Retrieved Customize+ data from Mare for {character.Name.TextValue}.");
                            cPlusData = customizePlusDataJson;
                            break;
                        }
                        else
                        {
                            Logger.Debug("C+ data for Player object was present but null or empty.");
                        }
                    }
                    else
                    {
                        Logger.Debug("CustomizePlusData dictionary does not contain an entry for the Player object kind.");
                    }
                }
            }

            if (string.IsNullOrEmpty(cPlusData))
            {
                Logger.Debug($"No valid C+ data found in any matching Mare pairs for {character.Name.TextValue}.");
            }
        }
        catch (Exception e)
        {
            Logger.Error("An exception occurred while reflecting into Mare Synchronos.", e);
        }

        return cPlusData;
    }
    public Guid? SetCustomizePlusScale(IntPtr address, string scale) => _customize.SetScale(address, scale);
    public void RevertCustomizePlusScale(Guid profileId) => _customize.Revert(profileId);
}
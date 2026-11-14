using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using Sphene.Interop.Ipc;
using Sphene.SpheneConfiguration.Models;
using Sphene.Services.Mediator;
using Sphene.UI.Design;

namespace Sphene.UI;

/// <summary>
/// Third-party plugin availability, installation and repository management.
/// Tracks which integration plugins are loaded (updated on the framework
/// thread) and installs/enables them through Dalamud internals via reflection
/// · Dalamud exposes no public API for this.
/// Also renders the plugin status rows for the setup flow.
/// </summary>
public sealed class PluginInstallerService : DisposableMediatorSubscriberBase
{
    private readonly IpcManager _ipcManager;
    private readonly IDalamudPluginInterface _pluginInterface;

    private bool _brioExists;
    private bool _bypassEmoteExists;
    private bool _customizePlusExists;
    private bool _glamourerExists;
    private bool _heelsExists;
    private bool _honorificExists;
    private bool _moodlesExists;
    private bool _penumbraExists;
    private bool _petNamesExists;

    public const string SeaOfStarsRepoUrl = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/main/repo.json";
    public const string BypassEmoteRepoUrl = "https://raw.githubusercontent.com/Aspher0/BypassEmote/refs/heads/main/repo.json";

    public PluginInstallerService(ILogger<PluginInstallerService> logger,
        IpcManager ipcManager, IDalamudPluginInterface pluginInterface,
        SpheneMediator mediator) : base(logger, mediator)
    {
        _ipcManager = ipcManager;
        _pluginInterface = pluginInterface;

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) =>
        {
            _penumbraExists = _ipcManager.Penumbra.APIAvailable;
            _glamourerExists = _ipcManager.Glamourer.APIAvailable;
            _customizePlusExists = _ipcManager.CustomizePlus.APIAvailable;
            _heelsExists = _ipcManager.Heels.APIAvailable;
            _honorificExists = _ipcManager.Honorific.APIAvailable;
            _moodlesExists = _ipcManager.Moodles.APIAvailable;
            _petNamesExists = _ipcManager.PetNames.APIAvailable;
            _brioExists = _ipcManager.Brio.APIAvailable;
            _bypassEmoteExists = _ipcManager.BypassEmote.APIAvailable;
        });
    }

    public enum RepoAddResult
    {
        NoChange,
        Added,
        Enabled
    }

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct PluginInstallState(bool IsInstalled, bool IsEnabled);

    /// <summary>Renders the mandatory/optional plugin checklist used by the intro flow.</summary>
    public bool DrawOtherPluginState()
    {
        SpheneUI.Text.Plain("Mandatory Plugins:");

        DrawPluginInstallStatus("Penumbra", _penumbraExists, "Penumbra", SeaOfStarsRepoUrl, "SeaOfStars Repo", 150);
        DrawPluginInstallStatus("Glamourer", _glamourerExists, "Glamourer", SeaOfStarsRepoUrl, "SeaOfStars Repo");

        SpheneUI.Text.Plain("Optional Plugins:");

        DrawPluginInstallStatus("SimpleHeels", _heelsExists, "SimpleHeels", SeaOfStarsRepoUrl, "SeaOfStars Repo", 150);
        DrawPluginInstallStatus("Customize+", _customizePlusExists, "CustomizePlus", SeaOfStarsRepoUrl, "SeaOfStars Repo");
        DrawPluginInstallStatus("Honorific", _honorificExists, "Honorific");
        DrawPluginInstallStatus("Moodles", _moodlesExists, "Moodles", SeaOfStarsRepoUrl, "SeaOfStars Repo");
        DrawPluginInstallStatus("PetNicknames", _petNamesExists, "PetRenamer");
        DrawPluginInstallStatus("Brio", _brioExists, "Brio", SeaOfStarsRepoUrl, "SeaOfStars Repo");
        DrawPluginInstallStatus("BypassEmote", _bypassEmoteExists, "BypassEmote", BypassEmoteRepoUrl, "BypassEmote Repo");

        if (!_penumbraExists || !_glamourerExists)
        {
            SpheneUI.Text.Colored("You need to install both Penumbra and Glamourer and keep them up to date to use Sphene.", ImGuiColors.DalamudRed);
            return false;
        }

        return true;
    }

    private void DrawPluginInstallStatus(string label, bool exists, string internalName, string? repoUrl = null, string? repoName = null, float? sameLineOffset = null)
    {
        if (sameLineOffset.HasValue)
            ImGui.SameLine(sameLineOffset.Value);
        else
            ImGui.SameLine();

        SpheneUI.Text.Colored(label, (exists ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed));

        if (!exists)
        {
            if (ImGui.IsItemClicked())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(repoUrl) && !string.IsNullOrEmpty(repoName))
                        {
                            Logger.LogDebug("Adding repo {RepoName} for {Plugin}...", repoName, internalName);
                            await AddRepoViaReflectionAsync(repoUrl, repoName).ConfigureAwait(false);
                            await Task.Delay(1000).ConfigureAwait(false);
                        }

                        Logger.LogDebug("Installing {Plugin}...", internalName);
                        await InstallPluginViaReflectionAsync(internalName).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to install {Plugin}", internalName);
                        Mediator.Publish(new NotificationMessage("Error", $"Failed to install {label}. Check logs.", NotificationType.Error));
                    }
                });

                Mediator.Publish(new NotificationMessage(label, "Installation started...", NotificationType.Info));
            }
            SpheneUI.AttachTooltip($"{label} is unavailable. Click to install.");
        }
        else
        {
            SpheneUI.AttachTooltip($"{label} is available and up to date.");
        }
    }

    public void OpenPluginInstaller(string searchText)
    {
        try
        {
            _pluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.AllPlugins, searchText);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to open plugin installer");
        }
    }

    public PluginInstallState GetPluginInstallState(params string[] internalNames)
    {
        foreach (var internalName in internalNames)
        {
            var plugin = _pluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
            if (plugin == null) continue;

            var enabledProp = plugin.GetType().GetProperty("IsEnabled");
            if (enabledProp?.GetValue(plugin) is bool enabledValue)
            {
                return new PluginInstallState(true, enabledValue);
            }

            var loadedProp = plugin.GetType().GetProperty("IsLoaded");
            if (loadedProp?.GetValue(plugin) is bool loadedValue)
            {
                return new PluginInstallState(true, loadedValue);
            }

            return new PluginInstallState(true, true);
        }

        return new PluginInstallState(false, false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarQube", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "Accessing internal Dalamud methods for plugin management.")]
    public async Task<RepoAddResult> AddRepoViaReflectionAsync(string url, string name)
    {
        try
        {
            Logger.LogDebug("Starting reflection-based repo addition...");

            var assembly = typeof(IDalamudPluginInterface).Assembly;
            var serviceType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "Service`1", StringComparison.Ordinal) && t.IsGenericType);
            var configType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "DalamudConfiguration", StringComparison.Ordinal));
            var pluginManagerType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "PluginManager", StringComparison.Ordinal));

            if (serviceType == null || configType == null || pluginManagerType == null)
            {
                Logger.LogError("Could not find Service<>, DalamudConfiguration, or PluginManager types.");
                return RepoAddResult.NoChange;
            }

            var configService = serviceType.MakeGenericType(configType);
            var configGetter = configService.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (configGetter == null)
            {
                Logger.LogError("Could not find Service.Get() method.");
                return RepoAddResult.NoChange;
            }

            var dalamudConfig = configGetter.Invoke(null, null);
            if (dalamudConfig == null)
            {
                Logger.LogError("Could not retrieve DalamudConfiguration instance.");
                return RepoAddResult.NoChange;
            }

            var repoListProp = configType.GetProperty("ThirdPartyRepositories", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? configType.GetProperty("ThirdRepoList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (repoListProp == null)
            {
                Logger.LogError("Could not find ThirdPartyRepositories or ThirdRepoList property.");
                return RepoAddResult.NoChange;
            }

            var repoList = repoListProp.GetValue(dalamudConfig) as IList;
            if (repoList == null)
            {
                Logger.LogError("ThirdPartyRepositories is null or not an IList.");
                return RepoAddResult.NoChange;
            }

            // Check if already exists
            bool alreadyExists = false;
            bool wasEnabled = false;
            foreach (var repo in repoList)
            {
                var urlProp = repo.GetType().GetProperty("Url");
                var currentUrl = urlProp?.GetValue(repo)?.ToString();
                if (string.Equals(currentUrl, url, StringComparison.Ordinal))
                {
                    Logger.LogDebug("Repository already exists in runtime config.");
                    alreadyExists = true;
                    // Ensure it is enabled
                    var enabledProp = repo.GetType().GetProperty("IsEnabled");
                    if (enabledProp?.GetValue(repo) is bool isEnabled)
                    {
                        wasEnabled = isEnabled;
                        if (!isEnabled)
                        {
                            enabledProp.SetValue(repo, true);
                        }
                    }
                    break;
                }
            }

            var result = RepoAddResult.NoChange;

            if (!alreadyExists)
            {
                // Create new entry
                var repoType = repoList.GetType().GetGenericArguments()[0];
                var newRepo = Activator.CreateInstance(repoType);

                if (newRepo == null)
                {
                    Logger.LogError("Could not create ThirdPartyRepository instance.");
                    return RepoAddResult.NoChange;
                }

                repoType.GetProperty("Url")?.SetValue(newRepo, url);
                repoType.GetProperty("Name")?.SetValue(newRepo, name);
                repoType.GetProperty("IsEnabled")?.SetValue(newRepo, true);

                // Try to set IsThirdParty if it exists
                var isThirdPartyProp = repoType.GetProperty("IsThirdParty");
                if (isThirdPartyProp != null)
                {
                    isThirdPartyProp.SetValue(newRepo, true);
                }

                repoList.Add(newRepo);
                Logger.LogDebug("Added new repo to list.");
                result = RepoAddResult.Added;
            }
            else if (!wasEnabled)
            {
                Logger.LogDebug("Repository existed but was disabled. Enabled it.");
                result = RepoAddResult.Enabled;
            }

            if (result == RepoAddResult.NoChange)
            {
                return RepoAddResult.NoChange;
            }

            var queueSaveMethod = configType.GetMethod("QueueSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (queueSaveMethod != null)
            {
                queueSaveMethod.Invoke(dalamudConfig, null);
                Logger.LogDebug("Queued Dalamud configuration save.");
            }
            else
            {
                // Fallback to Save() if QueueSave() is missing (unlikely in recent Dalamud versions)
                var saveMethod = configType.GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (saveMethod != null)
                {
                    try
                    {
                        saveMethod.Invoke(dalamudConfig, null);
                        Logger.LogDebug("Saved Dalamud configuration.");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to Save() configuration (likely thread safety). Continuing...");
                    }
                }
            }

            Mediator.Publish(new NotificationMessage("BypassEmote", "Repository added to Dalamud Config.", NotificationType.Info));

            // Notify PluginManager to reload repos from config
            var pluginManagerService = serviceType.MakeGenericType(pluginManagerType);
            var pluginManagerGetter = pluginManagerService.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (pluginManagerGetter != null)
            {
                var pluginManager = pluginManagerGetter.Invoke(null, null);
                if (pluginManager != null)
                {
                    var setReposMethod = pluginManagerType.GetMethod("SetPluginReposFromConfigAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (setReposMethod != null)
                    {
                        Logger.LogDebug("Calling SetPluginReposFromConfigAsync(true)...");
                        var task = setReposMethod.Invoke(pluginManager, new object[] { true }) as Task;
                        if (task != null) await task.ConfigureAwait(false);
                        Logger.LogDebug("SetPluginReposFromConfigAsync completed.");
                    }
                    else
                    {
                        Logger.LogError("Could not find SetPluginReposFromConfigAsync method.");
                    }
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to add repo via reflection.");
            return RepoAddResult.NoChange;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarQube", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "Accessing internal Dalamud methods for plugin management.")]
    public async Task<bool> EnablePluginViaReflectionAsync(params string[] internalNames)
    {
        try
        {
            var assembly = typeof(IDalamudPluginInterface).Assembly;
            var serviceType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "Service`1", StringComparison.Ordinal) && t.IsGenericType);
            var pluginManagerType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "PluginManager", StringComparison.Ordinal));

            if (serviceType == null || pluginManagerType == null)
            {
                Logger.LogError("Could not find Service<> or PluginManager types.");
                return false;
            }

            var pluginManagerService = serviceType.MakeGenericType(pluginManagerType);
            var pluginManagerGetter = pluginManagerService.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (pluginManagerGetter == null)
            {
                Logger.LogError("Could not find Service.Get() method for PluginManager.");
                return false;
            }

            var pluginManager = pluginManagerGetter.Invoke(null, null);
            if (pluginManager == null)
            {
                Logger.LogError("Could not retrieve PluginManager instance.");
                return false;
            }

            var installedPluginsProp = pluginManagerType.GetProperty("InstalledPlugins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (installedPluginsProp == null)
            {
                Logger.LogError("Could not find InstalledPlugins property.");
                return false;
            }

            var installedPlugins = installedPluginsProp.GetValue(pluginManager) as IEnumerable;
            if (installedPlugins == null)
            {
                Logger.LogError("InstalledPlugins is null.");
                return false;
            }

            foreach (var internalName in internalNames)
            {
                foreach (var plugin in installedPlugins)
                {
                    var nameProp = plugin.GetType().GetProperty("InternalName");
                    var currentName = nameProp?.GetValue(plugin)?.ToString();
                    if (!string.Equals(currentName, internalName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var isLoadedProp = plugin.GetType().GetProperty("IsLoaded");
                    if (isLoadedProp?.GetValue(plugin) is bool isLoaded && isLoaded)
                    {
                        return true;
                    }

                    var loadMethod = plugin.GetType().GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (loadMethod == null)
                    {
                        Logger.LogError("Could not find LoadAsync on LocalPlugin.");
                        return false;
                    }

                    var parameters = loadMethod.GetParameters();
                    object? result = parameters.Length switch
                    {
                        1 => loadMethod.Invoke(plugin, new object[] { PluginLoadReason.Installer }),
                        2 => loadMethod.Invoke(plugin, new object[] { PluginLoadReason.Installer, false }),
                        _ => null
                    };

                    if (result is Task task) await task.ConfigureAwait(false);
                    return result != null;
                }
            }

            Logger.LogDebug("EnablePluginViaReflectionAsync: plugin not found in InstalledPlugins.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to enable plugin via reflection.");
        }

        return false;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarQube", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "Accessing internal Dalamud methods for plugin management.")]
    public async Task<bool> DisablePluginViaReflectionAsync(params string[] internalNames)
    {
        try
        {
            var assembly = typeof(IDalamudPluginInterface).Assembly;
            var serviceType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "Service`1", StringComparison.Ordinal) && t.IsGenericType);
            var pluginManagerType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "PluginManager", StringComparison.Ordinal));

            if (serviceType == null || pluginManagerType == null)
            {
                Logger.LogError("Could not find Service<> or PluginManager types.");
                return false;
            }

            var pluginManagerService = serviceType.MakeGenericType(pluginManagerType);
            var pluginManagerGetter = pluginManagerService.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (pluginManagerGetter == null)
            {
                Logger.LogError("Could not find Service.Get() method for PluginManager.");
                return false;
            }

            var pluginManager = pluginManagerGetter.Invoke(null, null);
            if (pluginManager == null)
            {
                Logger.LogError("Could not retrieve PluginManager instance.");
                return false;
            }

            var installedPluginsProp = pluginManagerType.GetProperty("InstalledPlugins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (installedPluginsProp == null)
            {
                Logger.LogError("Could not find InstalledPlugins property.");
                return false;
            }

            var installedPlugins = installedPluginsProp.GetValue(pluginManager) as IEnumerable;
            if (installedPlugins == null)
            {
                Logger.LogError("InstalledPlugins is null.");
                return false;
            }

            foreach (var internalName in internalNames)
            {
                foreach (var plugin in installedPlugins)
                {
                    var nameProp = plugin.GetType().GetProperty("InternalName");
                    var currentName = nameProp?.GetValue(plugin)?.ToString();
                    if (!string.Equals(currentName, internalName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var isLoadedProp = plugin.GetType().GetProperty("IsLoaded");
                    if (isLoadedProp?.GetValue(plugin) is bool isLoaded && !isLoaded)
                    {
                        return true;
                    }

                    var unloadMethod = plugin.GetType().GetMethod("UnloadAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (unloadMethod == null)
                    {
                        Logger.LogError("Could not find UnloadAsync on LocalPlugin.");
                        return false;
                    }

                    object? result;
                    var parameters = unloadMethod.GetParameters();
                    if (parameters.Length == 0)
                    {
                        result = unloadMethod.Invoke(plugin, null);
                    }
                    else if (parameters.Length == 1)
                    {
                        var paramType = parameters[0].ParameterType;
                        object? paramValue = paramType.IsEnum
                            ? Enum.Parse(paramType, "WaitBeforeDispose")
                            : Activator.CreateInstance(paramType);
                        result = unloadMethod.Invoke(plugin, new[] { paramValue });
                    }
                    else
                    {
                        result = null;
                    }

                    if (result is Task task) await task.ConfigureAwait(false);
                    return result != null;
                }
            }

            Logger.LogDebug("DisablePluginViaReflectionAsync: plugin not found in InstalledPlugins.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to disable plugin via reflection.");
        }

        return false;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarQube", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "Accessing internal Dalamud methods for plugin management.")]
    public async Task InstallPluginViaReflectionAsync(string pluginInternalName)
    {
        try
        {
            var assembly = typeof(IDalamudPluginInterface).Assembly;
            var serviceType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "Service`1", StringComparison.Ordinal) && t.IsGenericType);
            var pluginManagerType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "PluginManager", StringComparison.Ordinal));

            if (serviceType == null || pluginManagerType == null)
            {
                Logger.LogError("Could not find Service<> or PluginManager types.");
                return;
            }

            var pluginManagerService = serviceType.MakeGenericType(pluginManagerType);
            var pluginManagerGetter = pluginManagerService.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (pluginManagerGetter == null)
            {
                Logger.LogError("Could not find Service.Get() method for PluginManager.");
                return;
            }

            var pluginManager = pluginManagerGetter.Invoke(null, null);
            if (pluginManager == null)
            {
                Logger.LogError("Could not retrieve PluginManager instance.");
                return;
            }

            // Reload Plugin Masters
            var reloadMethod = pluginManagerType.GetMethod("ReloadPluginMastersAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (reloadMethod != null)
            {
                Logger.LogDebug("Reloading plugin masters...");
                var task = reloadMethod.Invoke(pluginManager, new object[] { true }) as Task;
                if (task != null) await task.ConfigureAwait(false);
            }

            // Get AvailablePlugins
            var availablePluginsProp = pluginManagerType.GetProperty("AvailablePlugins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (availablePluginsProp == null)
            {
                Logger.LogError("Could not find AvailablePlugins property.");
                return;
            }

            var availablePlugins = availablePluginsProp.GetValue(pluginManager) as IEnumerable;
            if (availablePlugins == null)
            {
                Logger.LogError("AvailablePlugins is null.");
                return;
            }

            object? targetManifest = null;
            foreach (var manifest in availablePlugins)
            {
                var internalNameProp = manifest.GetType().GetProperty("InternalName");
                var internalName = internalNameProp?.GetValue(manifest)?.ToString();

                if (string.Equals(internalName, pluginInternalName, StringComparison.Ordinal))
                {
                    targetManifest = manifest;
                    break;
                }
            }

            if (targetManifest == null)
            {
                Logger.LogError("Could not find manifest for {PluginName}.", pluginInternalName);
                Mediator.Publish(new NotificationMessage("Installation Failed", $"Could not find {pluginInternalName} in available plugins.", NotificationType.Error));
                return;
            }

            Logger.LogDebug("Found manifest for {PluginName}. Installing...", pluginInternalName);

            // InstallPluginAsync
            var installMethod = pluginManagerType.GetMethod("InstallPluginAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (installMethod == null)
            {
                Logger.LogError("Could not find InstallPluginAsync method.");
                return;
            }

            // PluginLoadReason.Installer = 2
            var reasonEnumType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "PluginLoadReason", StringComparison.Ordinal));
            if (reasonEnumType == null)
            {
                Logger.LogError("Could not find PluginLoadReason enum.");
                return;
            }

            var reasonValue = Enum.ToObject(reasonEnumType, 2);

            // Parameters: manifest, useTesting, reason, inheritedWorkingPluginId
            var parameters = new object?[] { targetManifest, false, reasonValue, null };

            var installTask = installMethod.Invoke(pluginManager, parameters) as Task;
            if (installTask != null)
            {
                await installTask.ConfigureAwait(false);
                Logger.LogDebug("Installation task completed.");
                Mediator.Publish(new NotificationMessage(pluginInternalName, "Installation started successfully.", NotificationType.Success));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during reflection-based installation.");
        }
    }
}

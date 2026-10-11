using System.Reflection;
using VisualHFT.Commons.Extensions;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Studies;
using VisualHFT.DataRetriever;
using VisualHFT.Enums;
using VisualHFT.PluginManager;
using VisualHFT.TriggerEngine;

namespace VisualHFT.Desktop.Hosting;

/// <summary>
/// Mac host for the same plugin contract the Windows shell uses.
/// Settings dialogs stay in the Windows app; this host edits the settings model directly.
/// </summary>
public static class DesktopPluginManager
{
    private static readonly List<IPlugin> Plugins = new();
    private static readonly object Gate = new();
    private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(typeof(DesktopPluginManager));

    public static IReadOnlyList<IPlugin> AllPlugins
    {
        get
        {
            lock (Gate)
                return Plugins.ToList();
        }
    }

    public static void LoadPlugins()
    {
        var directory = AppContext.BaseDirectory;
        lock (Gate)
        {
            foreach (var file in Directory.GetFiles(directory, "*.dll"))
            {
                Assembly assembly;
                try
                {
                    assembly = Assembly.LoadFrom(file);
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetExportedTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
                }
                catch (Exception ex)
                {
                    Log.Warn("Skipped assembly " + Path.GetFileName(file), ex);
                    continue;
                }

                foreach (var type in types)
                {
                    if (type.IsAbstract || !typeof(IPlugin).IsAssignableFrom(type))
                        continue;
                    if (Plugins.Any(p => p.GetType() == type))
                        continue;

                    try
                    {
                        if (Activator.CreateInstance(type) is not IPlugin plugin || string.IsNullOrEmpty(plugin.Name))
                            continue;
                        if (!LicenseManager.Instance.HasAccess(plugin.RequiredLicenseLevel))
                            continue;

                        plugin.Status = ePluginStatus.LOADING;
                        Plugins.Add(plugin);
                        Log.Info("Plugin loaded: " + plugin.Name);
                    }
                    catch (Exception ex)
                    {
                        HelperNotificationManager.Instance.AddNotification(
                            "Loading Plugins",
                            $"Plugin's assemblies located in {file} have failed to load.",
                            HelprNorificationManagerTypes.ERROR,
                            HelprNorificationManagerCategories.PLUGINS,
                            ex);
                    }
                }
            }
        }
    }

    public static IReadOnlyList<StudyDescriptor> GetSelectableStudies()
    {
        var result = new List<StudyDescriptor>();
        lock (Gate)
        {
            foreach (var plugin in Plugins)
            {
                if (plugin is IMultiStudy multi)
                {
                    var parentId = plugin.GetPluginUniqueID();
                    var settings = plugin.Settings;
                    var reason = settings is null ? "Plugin has no settings." : settings.GetConfigurationError();
                    if (multi.Studies == null)
                        continue;
                    foreach (var child in multi.Studies)
                    {
                        if (child == null || !child.EmitsMetric)
                            continue;
                        result.Add(new StudyDescriptor
                        {
                            Id = $"{parentId}|{child.TileTitle}",
                            DisplayName = child.TileTitle,
                            GroupName = multi.TileTitle,
                            TileToolTip = child.TileToolTip ?? string.Empty,
                            ProviderName = settings?.Provider?.ProviderName ?? string.Empty,
                            Symbol = settings?.Symbol ?? string.Empty,
                            IsConfigured = reason is null,
                            UnavailableReason = reason
                        });
                    }
                }
                else if (plugin is IStudy study)
                {
                    if (!study.EmitsMetric)
                        continue;
                    var settings = plugin.Settings;
                    var reason = settings is null ? "Plugin has no settings." : settings.GetConfigurationError();
                    result.Add(new StudyDescriptor
                    {
                        Id = plugin.GetPluginUniqueID(),
                        DisplayName = study.TileTitle,
                        GroupName = null,
                        TileToolTip = study.TileToolTip ?? string.Empty,
                        ProviderName = settings?.Provider?.ProviderName ?? string.Empty,
                        Symbol = settings?.Symbol ?? string.Empty,
                        IsConfigured = reason is null,
                        UnavailableReason = reason
                    });
                }
            }
        }
        return result;
    }

    public static async Task StartPluginsAsync()
    {
        List<Task> startTasks;
        lock (Gate)
        {
            startTasks = Plugins.Select(plugin => Task.Run(async () =>
            {
                try
                {
                    await StartPluginAsync(plugin);
                }
                catch (Exception ex)
                {
                    plugin.Status = ePluginStatus.STOPPED_FAILED;
                    HelperNotificationManager.Instance.AddNotification(
                        plugin.Name,
                        "Plugin failed to Start.",
                        HelprNorificationManagerTypes.ERROR,
                        HelprNorificationManagerCategories.PLUGINS,
                        ex);
                }
            })).ToList();
        }

        await Task.WhenAll(startTasks);
    }

    public static void StartPlugin(IPlugin plugin) =>
        _ = StartPluginAsync(plugin);

    public static void StopPlugin(IPlugin plugin) =>
        _ = StopPluginAsync(plugin);

    public static async Task RestartPluginAsync(IPlugin plugin)
    {
        await StopPluginAsync(plugin);
        await StartPluginAsync(plugin);
    }

    public static async Task StartPluginAsync(IPlugin plugin)
    {
        if (plugin.Status is ePluginStatus.MALFUNCTIONING or ePluginStatus.STARTING or ePluginStatus.STARTED)
            return;

        if (plugin is IDataRetriever dataRetriever)
            await dataRetriever.StartAsync();
        else if (plugin is IStudy study)
        {
            await study.StartAsync();
            study.OnCalculated += (_, e) =>
            {
                var providerName = plugin.Settings?.Provider?.ProviderName ?? string.Empty;
                var symbol = plugin.Settings?.Symbol ?? string.Empty;
                TriggerEngineService.RegisterMetric(
                    plugin.GetPluginUniqueID(),
                    plugin.Name,
                    providerName,
                    symbol,
                    e.Value.ToDouble(),
                    e.Timestamp);
            };
        }
        else if (plugin is IMultiStudy multi)
        {
            await multi.StartAsync();
            var parentId = plugin.GetPluginUniqueID();
            if (multi.Studies == null)
                return;
            foreach (var child in multi.Studies)
            {
                if (child == null || !child.EmitsMetric)
                    continue;
                var childTitle = child.TileTitle;
                child.OnCalculated += (_, e) =>
                {
                    var providerName = plugin.Settings?.Provider?.ProviderName ?? string.Empty;
                    var symbol = plugin.Settings?.Symbol ?? string.Empty;
                    TriggerEngineService.RegisterMetric(
                        $"{parentId}|{childTitle}",
                        childTitle,
                        providerName,
                        symbol,
                        e.Value.ToDouble(),
                        e.Timestamp);
                };
            }
        }
    }

    public static async Task StopPluginAsync(IPlugin plugin)
    {
        try
        {
            var waits = 0;
            while (plugin.Status == ePluginStatus.STARTING && waits++ < 80)
                await Task.Delay(50);

            if (plugin.Status is ePluginStatus.STOPPED or ePluginStatus.STOPPED_FAILED)
                return;

            if (plugin is IDataRetriever dataRetriever)
                await dataRetriever.StopAsync();
            else if (plugin is IStudy study)
                await study.StopAsync();
            else if (plugin is IMultiStudy multi)
                await multi.StopAsync();

            plugin.Status = ePluginStatus.STOPPED;
        }
        catch (Exception ex)
        {
            plugin.Status = ePluginStatus.STOPPED_FAILED;
            HelperNotificationManager.Instance.AddNotification(
                plugin.Name,
                "Plugin failed to Stop.",
                HelprNorificationManagerTypes.ERROR,
                HelprNorificationManagerCategories.PLUGINS,
                ex);
        }
    }

    public static void UnloadPlugins()
    {
        lock (Gate)
        {
            foreach (var plugin in Plugins.OfType<IDisposable>())
            {
                try
                {
                    plugin.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to dispose " + plugin.GetType().Name, ex);
                }
            }
        }
    }
}

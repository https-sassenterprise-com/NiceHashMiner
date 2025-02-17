﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MinerPluginLoader;
using Newtonsoft.Json;
using NHM.Common;
using NHMCore.Configs;
using NHM.Common.Enums;
using NHM.MinersDownloader;
using NHMCore.Utils;
using System.Globalization;
using System.Collections.Concurrent;

namespace NHMCore.Mining.Plugins
{
    public static class MinerPluginsManager
    {
#warning "NO MORE ENABLE_EXTERNAL_PLUGINS. IntegratedPluginsOnly should be removed and deleted AND EXTERNAL PLUGINS SHOULD BE ALWAYS ENABLED"
        public static bool IntegratedPluginsOnly => false;

        static MinerPluginsManager()
        {
            var integratedPlugins = new List<IntegratedPlugin>
            {
                // testing 
                #if INTEGRATE_BrokenMiner_PLUGIN
                new BrokenPluginIntegratedPlugin(),
                #endif
                #if INTEGRATE_ExamplePlugin_PLUGIN
                new ExamplePluginIntegratedPlugin(),
                #endif

                // open source
                new CCMinerMTPIntegratedPlugin(),
                new CCMinerTpruvotIntegratedPlugin(),
                new SGminerAvemoreIntegratedPlugin(),
                new SGminerGMIntegratedPlugin(),
                new XmrStakIntegratedPlugin(),
#if INTEGRATE_CpuMinerOpt_PLUGIN
                new CPUMinerOptIntegratedPlugin(),
#endif
#if INTEGRATE_Ethminer_PLUGIN
                new EthminerIntegratedPlugin(),
#endif

// 3rd party
#if INTEGRATE_EWBF_PLUGIN
                new EWBFIntegratedPlugin(),
#endif
                new GMinerIntegratedPlugin(),
                new NBMinerIntegratedPlugin(),
                new PhoenixIntegratedPlugin(),
                new TeamRedMinerIntegratedPlugin(),
                new TRexIntegratedPlugin(),
#if INTEGRATE_TTMiner_PLUGIN
                new TTMinerIntegratedPlugin(),
#endif
                new ClaymoreDual14IntegratedPlugin(),

#if INTEGRATE_NanoMiner_PLUGIN
                new NanoMinerIntegratedPlugin(),
#endif
#if INTEGRATE_WildRig_PLUGIN
                new WildRigIntegratedPlugin(),
#endif
#if INTEGRATE_CryptoDredge_PLUGIN
                new CryptoDredgeIntegratedPlugin(),
#endif
#if INTEGRATE_BMiner_PLUGIN
                new BMinerIntegratedPlugin(),
#endif
#if INTEGRATE_ZEnemy_PLUGIN
                new ZEnemyIntegratedPlugin(),
#endif
#if INTEGRATE_LolMinerBeam_PLUGIN
                new LolMinerIntegratedPlugin(),
#endif
#if INTEGRATE_SRBMiner_PLUGIN
                new SRBMinerIntegratedPlugin(),
#endif
#if INTEGRATE_XMRig_PLUGIN
                new XMRigIntegratedPlugin(),
#endif
#if INTEGRATE_MiniZ_PLUGIN
                new MiniZIntegratedPlugin(),
#endif

                // service plugin
                EthlargementIntegratedPlugin.Instance,

                // plugin dependencies
                VC_REDIST_x64_2015_DEPENDENCY_PLUGIN.Instance
            };
            var filteredIntegratedPlugins = integratedPlugins.Where(p => SupportedPluginsFilter.IsSupported(p.PluginUUID)).ToList();
            foreach (var integratedPlugin in filteredIntegratedPlugins)
            {
                PluginContainer.Create(integratedPlugin);
            }
        }

        public static void InitIntegratedPlugins()
        {
            foreach (var plugin in PluginContainer.PluginContainers.Where(p => p.IsIntegrated))
            {
                if (!plugin.IsInitialized)
                {
                    plugin.InitPluginContainer();
                }
                if (plugin.Enabled)
                {
                    plugin.AddAlgorithmsToDevices();
                }
                else
                {
                    plugin.RemoveAlgorithmsFromDevices();
                }
            }

            // global scope here
            var is3rdPartyEnabled = ConfigManager.GeneralConfig.Use3rdPartyMiners == Use3rdPartyMiners.YES;
            EthlargementIntegratedPlugin.Instance.ServiceEnabled = ConfigManager.GeneralConfig.UseEthlargement && Helpers.IsElevated && is3rdPartyEnabled;
            Logger.Info("MinerPluginsManager", "Finished initialization of miners.");
        }

        // API data
        private static List<PluginPackageInfo> OnlinePlugins { get; set; }
        public static Dictionary<string, PluginPackageInfoCR> Plugins { get; set; } = new Dictionary<string, PluginPackageInfoCR>();

        //private static Dictionary<string, IMinerPlugin> MinerPlugins { get => MinerPluginHost.MinerPlugin; }

        public static IEnumerable<PluginPackageInfoCR> RankedPlugins
        {
            get
            {
                return Plugins
                    .Select(kvp => kvp.Value)
                    .OrderByDescending(info => info.HasNewerVersion)
                    .ThenByDescending(info => info.OnlineSupportedDeviceCount)
                    .ThenBy(info => info.PluginName);
            }
        }

        public static void LoadMinerPlugins()
        {
            // TODO only integrated
            InitIntegratedPlugins();
            if (IntegratedPluginsOnly) return;
            var loadedPlugins = MinerPluginHost.LoadPlugins(Paths.MinerPluginsPath());
            foreach (var pluginUUID in loadedPlugins)
            {
                var externalPlugin = MinerPluginHost.MinerPlugin[pluginUUID];
                var plugin = PluginContainer.Create(externalPlugin);
                if (!plugin.IsInitialized)
                {
                    plugin.InitPluginContainer();
                }
                if (plugin.Enabled)
                {
                    plugin.AddAlgorithmsToDevices();
                }
                else
                {
                    plugin.RemoveAlgorithmsFromDevices();
                }
            }
            // cross reference local and online list
            CrossReferenceInstalledWithOnline();
        }

        public static async Task DevicesCrossReferenceIDsWithMinerIndexes()
        {
            // get devices
            var baseDevices = AvailableDevices.Devices.Select(dev => dev.BaseDevice);
            var checkPlugins = PluginContainer.PluginContainers
                .Where(p => p.IsCompatible)
                .Where(p => p.Enabled)
                .ToArray();
            foreach (var plugin in checkPlugins)
            {
                await plugin.DevicesCrossReference(baseDevices);
            }
        }

        public static async Task DownloadMissingMinersBins(IProgress<(string loadMessageText, int prog)> progress, CancellationToken stop)
        {
            var checkPlugins = PluginContainer.PluginContainers
                .Where(p => p.IsCompatible)
                .Where(p => p.Enabled)
                .ToArray();

            foreach (var plugin in checkPlugins)
            {
                var urls = plugin.GetMinerBinsUrls().ToList();
                var missingFiles = plugin.CheckBinaryPackageMissingFiles();
                var hasMissingFiles = missingFiles.Any();
                var hasUrls = urls.Any();
                if (hasMissingFiles && hasUrls && !plugin.IsBroken)
                {
                    Logger.Info("MinerPluginsManager", $"Downloading missing files for {plugin.PluginUUID}-{plugin.Name}");
                    var downloadProgress = new Progress<int>(perc => progress?.Report((Translations.Tr("Downloading {0} %", $"{plugin.Name} {perc}"), perc)));
                    var unzipProgress = new Progress<int>(perc => progress?.Report((Translations.Tr("Unzipping {0} %", $"{plugin.Name} {perc}"), perc)));
                    await DownloadInternalBins(plugin.PluginUUID, urls.ToList(), downloadProgress, unzipProgress, stop);
                }
            }
        }

        public static async Task UpdateMinersBins(IProgress<(string loadMessageText, int prog)> progress, CancellationToken stop)
        {
            var checkPlugins = PluginContainer.PluginContainers
                .Where(p => p.IsCompatible)
                .Where(p => p.Enabled)
                .ToArray();

            foreach (var plugin in checkPlugins)
            {
                var urls = plugin.GetMinerBinsUrls();
                var hasUrls = urls.Count() > 0;
                var versionMismatch = plugin.IsVersionMismatch;
                if (versionMismatch && hasUrls && !plugin.IsBroken)
                {
                    Logger.Info("MinerPluginsManager", $"Version mismatch for {plugin.PluginUUID}-{plugin.Name}. Downloading...");
                    var downloadProgress = new Progress<int>(perc => progress?.Report((Translations.Tr("Downloading {0} %", $"{plugin.Name} {perc}"), perc)));
                    var unzipProgress = new Progress<int>(perc => progress?.Report((Translations.Tr("Unzipping {0} %", $"{plugin.Name} {perc}"), perc)));
                    await DownloadInternalBins(plugin.PluginUUID, urls.ToList(), downloadProgress, unzipProgress, stop);
                }
            }
        }

        // for now integrated only
        public static List<string> GetMissingMiners()
        {
            var checkPlugins = PluginContainer.PluginContainers
                .Where(p => p.IsCompatible)
                .Where(p => p.Enabled)
                .ToArray();

            var ret = new List<string>();
            foreach (var plugin in checkPlugins)
            {
                ret.AddRange(plugin.CheckBinaryPackageMissingFiles());
            }
            return ret;
        }

        public static bool HasMinerUpdates()
        {
            var checkPlugins = PluginContainer.PluginContainers
                .Where(p => p.IsCompatible)
                .Where(p => p.Enabled)
                .Where(p => p.IsVersionMismatch)
                .ToArray();


            return checkPlugins.Count() > 0;
        }

        private static void RemovePluginAlgorithms(string pluginUUID)
        {
            foreach (var dev in AvailableDevices.Devices)
            {
                dev.RemovePluginAlgorithms(pluginUUID);
            }
        }

        public static void Remove(string pluginUUID)
        {
            try
            {
                var deletePath = Path.Combine(Paths.MinerPluginsPath(), pluginUUID);
                MinerPluginHost.MinerPlugin.Remove(pluginUUID);
                var oldPlugins = PluginContainer.PluginContainers.Where(p => p.PluginUUID == pluginUUID).ToArray();
                foreach (var old in oldPlugins)
                {
                    PluginContainer.RemovePluginContainer(old);
                }
                RemovePluginAlgorithms(pluginUUID);

                Plugins[pluginUUID].LocalInfo = null;
                // TODO we might not have any online reference so remove it in this case
                if (Plugins[pluginUUID].OnlineInfo == null)
                {
                    Plugins.Remove(pluginUUID);
                }

                CrossReferenceInstalledWithOnline();
                // TODO before deleting you will need to unload the dll
                if (Directory.Exists(deletePath))
                {
                    Directory.Delete(deletePath, true);
                }
            } catch(Exception e)
            {
                Logger.Error("MinerPluginsManager", $"Error occured while removing {pluginUUID} plugin: {e.Message}");
            }       
        }

#warning "blocking method!!! Make it non blocking and change where it gets called"
        public static void CrossReferenceInstalledWithOnline()
        {
            // first go over the installed plugins
            // TODO rename installed to externalInstalledPlugin
            var checkPlugins = PluginContainer.PluginContainers
                .Where(p => !p.IsIntegrated)
                //.Where(p => p.IsCompatible)
                //.Where(p => p.Enabled)
                .ToArray();
            foreach (var installed in checkPlugins)
            {
                var uuid = installed.PluginUUID;
                var localPluginInfo = new PluginPackageInfo
                {
                    PluginAuthor = installed.Author,
                    PluginName = installed.Name,
                    PluginUUID = uuid,
                    PluginVersion = installed.Version,
                    // other stuff is not inside the plugin
                };
                if (Plugins.ContainsKey(uuid) == false)
                {
                    Plugins[uuid] = new PluginPackageInfoCR{};
                }
                Plugins[uuid].LocalInfo = localPluginInfo;
            }

            // get online list and check what we have and what is online
            if (GetOnlineMinerPlugins() == false || OnlinePlugins == null) return;

            foreach (var online in OnlinePlugins)
            {
                var uuid = online.PluginUUID;
                if (Plugins.ContainsKey(uuid) == false)
                {
                    Plugins[uuid] = new PluginPackageInfoCR{};
                }
                Plugins[uuid].OnlineInfo = online;
                if (online.SupportedDevicesAlgorithms != null)
                {
                    var supportedDevices = online.SupportedDevicesAlgorithms
                        .Where(kvp => kvp.Value.Count > 0)
                        .Select(kvp => kvp.Key);
                    var devRank = AvailableDevices.Devices
                        .Where(d => supportedDevices.Contains(d.DeviceType.ToString()))
                        .Count();
                    Plugins[uuid].OnlineSupportedDeviceCount = devRank;
                }
                
            }
        }

        public static List<string> GetPluginUUIDsAndVersionsList()
        {
            var ret = new List<string>();
            var checkPlugins = PluginContainer.PluginContainers
                .Where(p => p.IsCompatible)
                .Where(p => p.Enabled)
                .ToArray();
            foreach (var integrated in checkPlugins)
            {
                ret.Add($"{integrated.PluginUUID}-{integrated.Version.Major}.{integrated.Version.Minor}");
            }
            return ret;
        }


        private class NoKeepAlivesWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var request = base.GetWebRequest(address);
                if (request is HttpWebRequest)
                {
                    ((HttpWebRequest)request).KeepAlive = false;
                }

                return request;
            }
        }

        // TODO this here is blocking
        public static bool GetOnlineMinerPlugins()
        {
            try
            {
                using (var client = new NoKeepAlivesWebClient())
                {
                    string s = client.DownloadString(Links.PluginsJsonApiUrl);
                    //// local fake string
                    //string s = Properties.Resources.pluginJSON;
                    var onlinePlugins = JsonConvert.DeserializeObject<List<PluginPackageInfo>>(s, new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore,
                        MissingMemberHandling = MissingMemberHandling.Ignore,
                        Culture = CultureInfo.InvariantCulture
                    });
                    OnlinePlugins = onlinePlugins;
                }

                return true;
            } catch(Exception e)
            {
                Logger.Error("MinerPluginsManager", $"Error occured while getting online miner plugins: {e.Message}");
            }
            return false;
        }

        public static PluginContainer GetPluginWithUuid(string pluginUuid)
        {
            var ret = PluginContainer.PluginContainers.FirstOrDefault(p => p.PluginUUID == pluginUuid);
            return ret;
        }

#region DownloadingInstalling

        public static async Task DownloadInternalBins(string pluginUUID, List<string> urls, IProgress<int> downloadProgress, IProgress<int> unzipProgress, CancellationToken stop)
        {
            var installingPluginBinsPath = Path.Combine(Paths.MinerPluginsPath(), pluginUUID, "bins");
            try
            {
                if (Directory.Exists(installingPluginBinsPath)) Directory.Delete(installingPluginBinsPath, true);
                //downloadAndInstallUpdate("Starting");
                Directory.CreateDirectory(installingPluginBinsPath);
                var installedBins = false;
                foreach (var url in urls)
                {
                    // download plugin dll
                    var downloadMinerBinsResult = await MinersDownloadManager.DownloadFileAsync(url, installingPluginBinsPath, "miner_bins", downloadProgress, stop);
                    var binsPackageDownloaded = downloadMinerBinsResult.downloadedFilePath;
                    var downloadMinerBinsOK = downloadMinerBinsResult.success;
                    if (!downloadMinerBinsOK || stop.IsCancellationRequested) return;
                    // unzip 
                    var binsUnzipPath = installingPluginBinsPath; // Path.Combine(installingPluginPath, "bins");
                    var unzipMinerBinsOK = await ArchiveHelpers.ExtractFileAsync(binsPackageDownloaded, binsUnzipPath, unzipProgress, stop);
                    if (stop.IsCancellationRequested) return;
                    if (unzipMinerBinsOK)
                    {
                        installedBins = true;
                        File.Delete(binsPackageDownloaded);
                        break;
                    }   
                }
                if (!installedBins)
                {
                    Logger.Error("MinerPluginsManager", $"Miners bins of {pluginUUID} not installed");
                }
            }
            catch (Exception e)
            {
                Logger.Error("MinerPluginsManager", $"Installation of {pluginUUID} failed: ${e.Message}");
            }
        }

        private static ConcurrentDictionary<string, MinerPluginInstallTask> MinerPluginInstallTasks = new ConcurrentDictionary<string, MinerPluginInstallTask>();

        public static void InstallAddProgress(string pluginUUID, IProgress<Tuple<PluginInstallProgressState, int>> progress)
        {
            if (MinerPluginInstallTasks.TryGetValue(pluginUUID, out var installTask))
            {
                installTask.AddProgress(progress);
            }
        }

        public static void InstallRemoveProgress(string pluginUUID, IProgress<Tuple<PluginInstallProgressState, int>> progress)
        {
            if (MinerPluginInstallTasks.TryGetValue(pluginUUID, out var installTask))
            {
                installTask.RemoveProgress(progress);
            }
        }

        public static void TryCancelInstall(string pluginUUID)
        {
            if (MinerPluginInstallTasks.TryGetValue(pluginUUID, out var installTask))
            {
                installTask.TryCancelInstall();
            }
        }

        public static async Task DownloadAndInstall(string pluginUUID, IProgress<Tuple<PluginInstallProgressState, int>> progress)
        {
            // TODO skip install if alredy in progress
            var addSuccess = false;
            using (var minerInstall = new MinerPluginInstallTask())
            {
                try
                {
                    var pluginPackageInfo = Plugins[pluginUUID];
                    addSuccess = MinerPluginInstallTasks.TryAdd(pluginUUID, minerInstall);
                    progress?.Report(Tuple.Create(PluginInstallProgressState.Pending, 0));
                    minerInstall.AddProgress(progress);
                    await DownloadAndInstall(pluginPackageInfo, minerInstall, minerInstall.CancelInstallToken);
                }
                finally
                {
                    if (addSuccess)
                    {
                        MinerPluginInstallTasks.TryRemove(pluginUUID, out var _);
                    }
                }
            }
        }

        internal static async Task DownloadAndInstall(PluginPackageInfoCR plugin, IProgress<Tuple<PluginInstallProgressState, int>> progress, CancellationToken stop)
        {
            var downloadPluginProgressChangedEventHandler = new Progress<int>(perc => progress?.Report(Tuple.Create(PluginInstallProgressState.DownloadingPlugin, perc)));
            var zipProgressPluginChangedEventHandler = new Progress<int>(perc => progress?.Report(Tuple.Create(PluginInstallProgressState.ExtractingPlugin, perc)));
            var downloadMinerProgressChangedEventHandler = new Progress<int>(perc => progress?.Report(Tuple.Create(PluginInstallProgressState.DownloadingMiner, perc)));
            var zipProgressMinerChangedEventHandler = new Progress<int>(perc => progress?.Report(Tuple.Create(PluginInstallProgressState.ExtractingMiner, perc)));

            var finalState = PluginInstallProgressState.Pending;
            const string installingPrefix = "installing_";
            var installingPluginPath = Path.Combine(Paths.MinerPluginsPath(), $"{installingPrefix}{plugin.PluginUUID}");
            try
            {
                if (Directory.Exists(installingPluginPath)) Directory.Delete(installingPluginPath, true);
                //downloadAndInstallUpdate("Starting");
                Directory.CreateDirectory(installingPluginPath);

                // download plugin dll
                progress?.Report(Tuple.Create(PluginInstallProgressState.PendingDownloadingPlugin, 0));
                var downloadPluginResult = await MinersDownloadManager.DownloadFileAsync(plugin.PluginPackageURL, installingPluginPath, "plugin", downloadPluginProgressChangedEventHandler, stop);
                var pluginPackageDownloaded = downloadPluginResult.downloadedFilePath;
                var downloadPluginOK = downloadPluginResult.success;
                if (!downloadPluginOK || stop.IsCancellationRequested)
                {
                    finalState = stop.IsCancellationRequested ? PluginInstallProgressState.Canceled : PluginInstallProgressState.FailedDownloadingPlugin;
                    return;
                }
                // unzip 
                progress?.Report(Tuple.Create(PluginInstallProgressState.PendingExtractingPlugin, 0));
                var unzipPluginOK = await ArchiveHelpers.ExtractFileAsync(pluginPackageDownloaded, installingPluginPath, zipProgressPluginChangedEventHandler, stop);
                if (!unzipPluginOK || stop.IsCancellationRequested)
                {
                    finalState = stop.IsCancellationRequested ? PluginInstallProgressState.Canceled : PluginInstallProgressState.FailedExtractingPlugin;
                    return;
                }
                File.Delete(pluginPackageDownloaded);

                // download plugin binary
                progress?.Report(Tuple.Create(PluginInstallProgressState.PendingDownloadingMiner, 0));
                var downloadMinerBinsResult = await MinersDownloadManager.DownloadFileAsync(plugin.MinerPackageURL, installingPluginPath, "miner_bins", downloadMinerProgressChangedEventHandler, stop);
                var binsPackageDownloaded = downloadMinerBinsResult.downloadedFilePath;
                var downloadMinerBinsOK = downloadMinerBinsResult.success;
                if (!downloadMinerBinsOK || stop.IsCancellationRequested)
                {
                    finalState = stop.IsCancellationRequested ? PluginInstallProgressState.Canceled : PluginInstallProgressState.FailedDownloadingMiner;
                    return;
                }
                // unzip 
                progress?.Report(Tuple.Create(PluginInstallProgressState.PendingExtractingMiner, 0));
                var binsUnzipPath = Path.Combine(installingPluginPath, "bins");
                var unzipMinerBinsOK = await ArchiveHelpers.ExtractFileAsync(binsPackageDownloaded, binsUnzipPath, zipProgressMinerChangedEventHandler, stop);
                if (!unzipMinerBinsOK || stop.IsCancellationRequested)
                {
                    finalState = stop.IsCancellationRequested ? PluginInstallProgressState.Canceled : PluginInstallProgressState.FailedExtractingMiner;
                    return;
                }
                File.Delete(binsPackageDownloaded);

                // TODO from here on add the failed plugin load state and success state
                var loadedPlugins = MinerPluginHost.LoadPlugin(installingPluginPath);
                if (loadedPlugins.Count() == 0)
                {
                    //downloadAndInstallUpdate($"Loaded ZERO PLUGINS");
                    finalState = stop.IsCancellationRequested ? PluginInstallProgressState.Canceled : PluginInstallProgressState.FailedPluginLoad;
                    Directory.Delete(installingPluginPath, true);
                    return;
                }

                //downloadAndInstallUpdate("Checking old plugin");
                var pluginPath = Path.Combine(Paths.MinerPluginsPath(), plugin.PluginUUID);
                // if there is an old plugin installed remove it
                if (Directory.Exists(pluginPath))
                {
                    // TODO consider saving the internal settings when updating the miner plugin
                    Directory.Delete(pluginPath, true);
                }
                //downloadAndInstallUpdate($"Loaded {loadedPlugins} PLUGIN");
                Directory.Move(installingPluginPath, pluginPath);
                // add or update plugins
                foreach (var pluginUUID in loadedPlugins)
                {
                    var newExternalPlugin = MinerPluginHost.MinerPlugin[pluginUUID];
                    // remove old
                    var oldPlugins = PluginContainer.PluginContainers.Where(p => p.PluginUUID == pluginUUID).ToArray();
                    foreach (var old in oldPlugins)
                    {
                        PluginContainer.RemovePluginContainer(old);
                    }
                    var newPlugin = PluginContainer.Create(newExternalPlugin);
                    var success = newPlugin.InitPluginContainer();
                    // TODO after add or remove plugins we should clean up the device settings
                    if (success)
                    {
                        newPlugin.AddAlgorithmsToDevices();
                        await newPlugin.DevicesCrossReference(AvailableDevices.Devices.Select(d => d.BaseDevice));
                    }
                    else
                    {
                        // TODO mark that this plugin wasn't loaded
                        Logger.Error("MinerPluginsManager", $"DownloadAndInstall unable to init and install {pluginUUID}");
                    }
                }
                // cross reference local and online list
                CrossReferenceInstalledWithOnline();
                finalState = stop.IsCancellationRequested ? PluginInstallProgressState.Canceled : PluginInstallProgressState.Success;
            }
            catch (Exception e)
            {
                Logger.Error("MinerPluginsManager", $"Installation of {plugin.PluginName}_{plugin.PluginVersion}_{plugin.PluginUUID} failed: ${e.Message}");
                //downloadAndInstallUpdate();
                finalState = stop.IsCancellationRequested ? PluginInstallProgressState.Canceled : PluginInstallProgressState.FailedUnknown;
            }
            finally
            {
                progress?.Report(Tuple.Create(finalState, 0));
            }
        }
#endregion DownloadingInstalling
    }
}

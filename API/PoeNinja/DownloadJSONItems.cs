using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NinjaPricer.API.PoeNinja.Models;

namespace NinjaPricer.API.PoeNinja;

public class DataDownloader
{
    private const string BaseUrl = "https://poe.ninja";

    private static string GetExchangeLink(string league, string type)
        => $"{BaseUrl}/poe2/api/economy/exchange/current/overview?league={league}&type={type}";

    private static string GetStashLink(string league, string type)
        => $"{BaseUrl}/poe2/api/economy/stash/current/item/overview?league={league}&type={type}";

    private int _updating;
    public CollectiveApiData CollectedData { get; set; }

    private class LeagueMetadata
    {
        public DateTime LastLoadTime { get; set; }
    }

    public Action<string> log { get; set; }
    public NinjaPricerSettings Settings { get; set; }
    public string DataDirectory { get; set; }

    // Mapping from category name -> API type parameter
    static readonly Dictionary<string, string> ExchangeCategoryMap = new()
    {
        { "Currency", "Currency" },
        { "Breach", "Breach" },
        { "Delirium", "Delirium" },
        { "Essences", "Essences" },
        { "Runes", "Runes" },
        { "Ritual", "Ritual" },
        { "Fragments", "Fragments" },
        { "UncutGems", "UncutGems" },
        { "Abyss", "Abyss" },
        { "Expedition", "Expedition" },
        { "Verisium", "Verisium" },
        { "LineageSupportGems", "LineageSupportGems" },
        { "SoulCores", "SoulCores" },
        { "Idols", "Idols" },
    };

    static readonly Dictionary<string, string> StashCategoryMap = new()
    {
        { "Weapons", "UniqueWeapons"},
        { "Armour", "UniqueArmours"},
        { "Accessories", "UniqueAccessories"},
        { "Flasks", "UniqueFlasks"},
        { "Jewels", "UniqueJewels"},
        // PoE2 0.5 retired UniqueMaps (HTTP 404); map-like uniques now live under UniqueTablets.
        // The distinct key also changes the on-disk cache filename to Tablets.json, so a stale
        // Maps.json backup from an older league can no longer surface wrong prices.
        { "Tablets", "UniqueTablets"},
        { "Charms", "UniqueCharms" },
        { "SanctumRelics", "UniqueSanctumRelics" },
    };

    public void StartDataReload(string league, bool forceRefresh)
    {
        log($"Getting data for {league}");

        if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0)
        {
            log("Update is already in progress");
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                log("Gathering Data from Poe.Ninja.");

                var newData = new CollectiveApiData();
                var tryWebFirst = forceRefresh;
                var metadataPath = Path.Join(DataDirectory, league, "meta.json");
                if (!tryWebFirst && Settings.DataSourceSettings.AutoReload)
                {
                    tryWebFirst = await IsLocalCacheStale(metadataPath);
                }

                // Load exchange (currency) categories
                foreach (var (key, type) in ExchangeCategoryMap)
                {
                    var fileName = $"{key}.json";
                    var url = GetExchangeLink(league, type);

                    var data = await LoadFromWebOrBackup<ExchangeOverview>(fileName, url, tryWebFirst);
                    if (data != null)
                    {
                        SetExchangeProperty(newData, key, data);
                    }
                }

                // Load stash (unique) categories
                foreach (var (key, type) in StashCategoryMap)
                {
                    var fileName = $"{key}.json";
                    var url = GetStashLink(league, type);

                    var data = await LoadFromWebOrBackup<StashOverview>(fileName, url, tryWebFirst);
                    if (data != null)
                    {
                        SetStashProperty(newData, key, data);
                    }
                }

                newData.DivineToExaltedRate = newData.DivineToExaltedRateRaw;

                new FileInfo(metadataPath).Directory?.Create();
                await File.WriteAllTextAsync(metadataPath, JsonConvert.SerializeObject(new LeagueMetadata { LastLoadTime = DateTime.UtcNow }));

                log("Finished Gathering Data from Poe.Ninja.");
                CollectedData = newData;
                log("Updated CollectedData.");
            }
            finally
            {
                Interlocked.Exchange(ref _updating, 0);
            }
        });
    }

    void SetExchangeProperty(CollectiveApiData data, string name, ExchangeOverview value)
    {
        switch (name)
        {
            case "Currency": data.Currency = value; break;
            case "Breach": data.Breach = value; break;
            case "Delirium": data.Delirium = value; break;
            case "Essences": data.Essences = value; break;
            case "Runes": data.Runes = value; break;
            case "Ritual": data.Ritual = value; break;
            case "Fragments": data.Fragments = value; break;
            case "UncutGems": data.UncutGems = value; break;
            case "Abyss": data.Abyss = value; break;
            case "Expedition": data.Expedition = value; break;
            case "Verisium": data.Verisium = value; break;
            case "LineageSupportGems": data.LineageSupportGems = value; break;
            case "SoulCores": data.SoulCores = value; break;
            case "Idols": data.Idols = value; break;
        }
    }

    void SetStashProperty(CollectiveApiData data, string name, StashOverview value)
    {
        switch (name)
        {
            case "Weapons": data.Weapons = value; break;
            case "Armour": data.Armour = value; break;
            case "Accessories": data.Accessories = value; break;
            case "Flasks": data.Flasks = value; break;
            case "Jewels": data.Jewels = value; break;
            case "Tablets": data.Tablets = value; break;
            case "Charms": data.Charms = value; break;
            case "SanctumRelics": data.SanctumRelics = value; break;
        }
    }

    private async Task<bool> IsLocalCacheStale(string metadataPath)
    {
        if (!File.Exists(metadataPath))
            return true;

        try
        {
            var metadata = JsonConvert.DeserializeObject<LeagueMetadata>(await File.ReadAllTextAsync(metadataPath));
            return DateTime.UtcNow - metadata.LastLoadTime > TimeSpan.FromMinutes(Settings.DataSourceSettings.ReloadPeriod);
        }
        catch (Exception ex)
        {
            if (Settings.DebugSettings.EnableDebugLogging)
                log($"Metadata loading failed: {ex}");
            return true;
        }
    }

    private async Task<T> LoadFromWebOrBackup<T>(string fileName, string url, bool tryWebFirst) where T : class
    {
        var backupFile = Path.Join(DataDirectory, Settings.DataSourceSettings.League.Value, fileName);

        if (tryWebFirst)
        {
            var webData = await LoadFromWeb<T>(fileName, url, backupFile);
            if (webData != null) return webData;
        }

        var backupData = await LoadFromBackup<T>(fileName, backupFile);
        if (backupData != null) return backupData;

        if (!tryWebFirst)
        {
            return await LoadFromWeb<T>(fileName, url, backupFile);
        }

        return null;
    }

    private async Task<T> LoadFromWeb<T>(string fileName, string url, string backupFile) where T : class
    {
        try
        {
            if (Settings.DebugSettings.EnableDebugLogging)
                log($"Downloading {fileName}");

            var json = await Utils.DownloadFromUrl(url);
            var data = JsonConvert.DeserializeObject<T>(json);

            if (Settings.DebugSettings.EnableDebugLogging)
                log($"{fileName} downloaded");

            try
            {
                new FileInfo(backupFile).Directory.Create();
                await File.WriteAllTextAsync(backupFile, JsonConvert.SerializeObject(data, Formatting.Indented));
            }
            catch (Exception ex)
            {
                var errorPath = backupFile + ".error";
                new FileInfo(errorPath).Directory.Create();
                await File.WriteAllTextAsync(errorPath, ex.ToString());
                if (Settings.DebugSettings.EnableDebugLogging)
                    log($"{fileName} save failed: {ex}");
            }

            return data;
        }
        catch (Exception ex)
        {
            if (Settings.DebugSettings.EnableDebugLogging)
                log($"{fileName} fresh data download failed: {ex}");
            return null;
        }
    }

    private async Task<T> LoadFromBackup<T>(string fileName, string backupFile) where T : class
    {
        if (File.Exists(backupFile))
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(await File.ReadAllTextAsync(backupFile));
            }
            catch (Exception backupEx)
            {
                if (Settings.DebugSettings.EnableDebugLogging)
                    log($"{fileName} backup data load failed: {backupEx}");
            }
        }
        else if (Settings.DebugSettings.EnableDebugLogging)
        {
            log($"No backup for {fileName}");
        }

        return null;
    }
}

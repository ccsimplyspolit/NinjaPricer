using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.PoEMemory.Models;
using Newtonsoft.Json;
using NinjaPricer.API.PoeNinja;
using NinjaPricer.API.PoeNinja.Models;
using CollectiveApiData = NinjaPricer.API.PoeNinja.CollectiveApiData;

namespace NinjaPricer;

public partial class NinjaPricer : BaseSettingsPlugin<NinjaPricerSettings>
{
    private string NinjaDirectory;
    private CollectiveApiData CollectedData => _downloader.CollectedData;
    private const string CustomUniqueArtMappingPath = "uniqueArtMapping.json";
    private const string DefaultUniqueArtMappingPath = "uniqueArtMapping.default.json";
    internal const string DefaultWav = "default.wav";
    public Dictionary<string, List<string>> UniqueArtMapping = new Dictionary<string, List<string>>();
    private readonly DataDownloader _downloader = new DataDownloader();
    private Dictionary<string, string> _soundFiles = [];
    private readonly HashSet<string> _preloadedSoundFiles = new(StringComparer.OrdinalIgnoreCase);

    public override bool Initialise()
    {
        _downloader.DataDirectory = Path.Join(DirectoryFullName, "poescoutdata");
        _downloader.Settings = Settings;
        _downloader.log = LogMessage;
        NinjaDirectory = Path.Join(DirectoryFullName, "NinjaData");
        Directory.CreateDirectory(NinjaDirectory);
        Input.RegisterKey(Settings.DebugSettings.InspectHoverHotkey.Value);

        UpdateLeagueList();
        _downloader.StartDataReload(Settings.DataSourceSettings.League.Value, false);

        Settings.DataSourceSettings.ReloadPrices.OnPressed += () => _downloader.StartDataReload(Settings.DataSourceSettings.League.Value, true);
        Settings.UniqueIdentificationSettings.RebuildUniqueItemArtMappingBackup.OnPressed += () =>
        {
            var mapping = GetGameFileUniqueArtMapping();
            if (mapping != null)
            {
                File.WriteAllText(Path.Join(DirectoryFullName, CustomUniqueArtMappingPath), JsonConvert.SerializeObject(mapping, Formatting.Indented));
            }
        };
        Settings.UniqueIdentificationSettings.IgnoreGameUniqueArtMapping.OnValueChanged += (_, _) =>
        {
            UniqueArtMapping = GetUniqueArtMapping();
        };
        Settings.DataSourceSettings.SyncCurrentLeague.OnValueChanged += (_, _) => SyncCurrentLeague();
        CustomItem.InitCustomItem(this);
        Settings.DebugSettings.ResetInspectedItem.OnPressed += () =>
        {
            _inspectedItem = null;
        };
        GameController.PluginBridge.SaveMethod("NinjaPrice.GetValue", (Entity e) =>
        {
            var customItem = new CustomItem(e, null);
            GetValue(customItem);
            return customItem.PriceData.MinChaosValue;
        });
        GameController.PluginBridge.SaveMethod("NinjaPrice.GetBaseItemTypeValue", (BaseItemType baseItemType) =>
        {
            var customItem = new CustomItem(baseItemType);
            GetValue(customItem);
            return customItem.PriceData.MinChaosValue;
        });

        Settings.SoundNotificationSettings.ResetEntityNotificationFlags.OnPressed += () =>
        {
            _soundPlayedTracker.Clear();
        };
        Settings.SoundNotificationSettings.OpenConfigDirectory.OnPressed += () =>
        {
            Process.Start("explorer.exe", ConfigDirectory);
        };
        Settings.SoundNotificationSettings.ReloadSoundList.OnPressed += ReloadSoundList;
        ReloadSoundList();

        return true;
    }

    public override void Dispose()
    {
        // Stop in-flight HTTP/file work before ExileCore2 tears down the plugin instance.  This
        // also prevents a queued league refresh from publishing a snapshot after unload.
        _downloader.Dispose();
        base.Dispose();
    }

    private void ReloadSoundList()
    {
        var defaultFilePath = Path.Join(ConfigDirectory, DefaultWav);
        if (!File.Exists(defaultFilePath))
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefaultWav);
            using var file = File.OpenWrite(defaultFilePath);
            stream.CopyTo(file);
        }

        _soundFiles = Directory.EnumerateFiles(ConfigDirectory, "*.wav")
            .Select(x => (Path.GetFileNameWithoutExtension(x), x))
            .DistinctBy(x => x.Item1, StringComparer.InvariantCultureIgnoreCase)
            .ToDictionary(x => x.Item1, x => x.x, StringComparer.InvariantCultureIgnoreCase);
        _preloadedSoundFiles.Clear();
    }

    public override void AreaChange(AreaInstance area)
    {
        _inspectedItem = null;
        _soundPlayedTracker.Clear();
        UniqueArtMapping = GetUniqueArtMapping();
        SyncCurrentLeague();
    }

    private void SyncCurrentLeague()
    {
        if (Settings.DataSourceSettings.SyncCurrentLeague)
        {
            var playerLeague = PlayerLeague;
            if (playerLeague != null)
            {
                if (!Settings.DataSourceSettings.League.Values.Contains(playerLeague))
                {
                    Settings.DataSourceSettings.League.Values.Add(playerLeague);
                }

                if (Settings.DataSourceSettings.League.Value != playerLeague)
                {
                    Settings.DataSourceSettings.League.Value = playerLeague;
                    _downloader.StartDataReload(Settings.DataSourceSettings.League.Value, false);
                }
            }
        }
    }

    private Dictionary<string, List<string>> GetUniqueArtMapping()
    {
        Dictionary<string, List<string>> mapping = null;
        if (!Settings.UniqueIdentificationSettings.IgnoreGameUniqueArtMapping &&
            GameController.Files.UniqueItemDescriptions.EntriesList.Count != 0 &&
            GameController.Files.ItemVisualIdentities.EntriesList.Count != 0)
        {
            mapping = GetGameFileUniqueArtMapping();
        }

        var customFilePath = Path.Join(DirectoryFullName, CustomUniqueArtMappingPath);
        if (File.Exists(customFilePath))
        {
            try
            {
                mapping ??= JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(customFilePath));
            }
            catch (Exception ex)
            {
                LogError($"Unable to load custom art mapping: {ex}");
            }
        }

        mapping ??= GetEmbeddedUniqueArtMapping();
        mapping ??= [];
        return mapping.ToDictionary(x => x.Key, x =>
            x.Value.Select(str => str.Replace('’', '\''))
            .Except(Settings.UniqueIdentificationSettings.ExcludedUniques.Content.Select(c => c.Value), 
                StringComparer.InvariantCultureIgnoreCase)
            .ToList());
    }

    private Dictionary<string, List<string>> GetEmbeddedUniqueArtMapping()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefaultUniqueArtMappingPath);
            if (stream == null)
            {
                if (Settings.DebugSettings.EnableDebugLogging)
                {
                    LogMessage($"Embedded stream {DefaultUniqueArtMappingPath} is missing");
                }

                return null;
            }

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            return JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(content);
        }
        catch (Exception ex)
        {
            LogError($"Unable to load embedded art mapping: {ex}");
            return null;
        }
    }

    private Dictionary<string, List<string>> GetGameFileUniqueArtMapping()
    {
        GameController.Files.UniqueItemDescriptions.ReloadIfEmptyOrZero();

        return GameController.Files.ItemVisualIdentities.EntriesList.Where(x => x.ArtPath != null)
            .GroupJoin(GameController.Files.UniqueItemDescriptions.EntriesList.Where(x => x.ItemVisualIdentity != null),
                x => x,
                x => x.ItemVisualIdentity, (ivi, descriptions) => (ivi.ArtPath, descriptions: descriptions.ToList()))
            .GroupBy(x => x.ArtPath, x => x.descriptions)
            .Select(x => (x.Key, Names: x
                .SelectMany(items => items)
                .Select(item => item.UniqueName?.Text)
                .Where(name => name != null)
                .Distinct()
                .ToList()))
            .Where(x => x.Names.Any())
            .ToDictionary(x => x.Key, x => x.Names);
    }

    private void UpdateLeagueList()
    {
        var leagueList = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        var playerLeague = PlayerLeague;
        if (playerLeague != null)
        {
            leagueList.Add(playerLeague);
        }

        var leagueUrls = new[]
        {
            "https://poe.ninja/poe2/api/economy/leagues",
            // Keep the older index-state route as a compatibility fallback for
            // temporary API deployments that do not expose economy/leagues.
            "https://poe.ninja/poe2/api/data/index-state",
        };

        foreach (var leagueUrl in leagueUrls)
        {
            try
            {
                var leagueListFromUrl = Utils.DownloadFromUrl(leagueUrl).GetAwaiter().GetResult();
                var names = ParsePoeNinjaLeagueNames(leagueListFromUrl);
                if (names.Count == 0)
                {
                    continue;
                }

                leagueList.UnionWith(names);
                break;
            }
            catch (Exception ex)
            {
                LogError($"Failed to download the poe.ninja league list from {leagueUrl}: {ex.Message}");
            }
        }

        leagueList.Add("Standard");
        leagueList.Add("Hardcore");

        if (!leagueList.Contains(Settings.DataSourceSettings.League.Value))
        {
            Settings.DataSourceSettings.League.Value = leagueList.MaxBy(x => x == playerLeague);
        }

        Settings.DataSourceSettings.League.SetListValues(leagueList.ToList());
    }

    private static HashSet<string> ParsePoeNinjaLeagueNames(string json)
    {
        var names = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);

        PoeNinjaLeague[] economyLeagues = null;
        try
        {
            economyLeagues = JsonConvert.DeserializeObject<PoeNinjaLeague[]>(json);
        }
        catch (JsonException)
        {
            // The compatibility response is an object with economyLeagues;
            // parse that shape below instead of treating it as a hard failure.
        }

        if (economyLeagues != null)
        {
            names.UnionWith(economyLeagues
                .Select(league => league.name)
                .Where(name => !string.IsNullOrWhiteSpace(name)));
        }

        if (names.Count > 0)
        {
            return names;
        }

        var legacyRoot = JsonConvert.DeserializeObject<LeagueRoot>(json);
        names.UnionWith(legacyRoot?.economyLeagues?
            .Select(league => league.name)
            .Where(name => !string.IsNullOrWhiteSpace(name)) ?? Enumerable.Empty<string>());
        return names;
    }

    private string PlayerLeague
    {
        get
        {
            var playerLeague = GameController.IngameState.ServerData.League;
            if (string.IsNullOrWhiteSpace(playerLeague))
            {
                playerLeague = null;
            }
            else
            {
                if (playerLeague.StartsWith("HC SSF "))
                {
                    playerLeague = $"HC {playerLeague["HC SSF ".Length..]}";
                }
                else if (playerLeague.StartsWith("SSF "))
                {
                    playerLeague = playerLeague["SSF ".Length..];
                }
            }

            return playerLeague;
        }
    }
}

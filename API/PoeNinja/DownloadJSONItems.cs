using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NinjaPricer.API.PoeNinja.Models;

namespace NinjaPricer.API.PoeNinja;

/// <summary>Downloads and caches the PoE2 economy/stash data used by NinjaPricer.</summary>
public sealed class DataDownloader : IDisposable
{
    private const string BaseUrl = "https://poe.ninja";
    private const int MaxCacheLeagueLength = 80;

    private static string GetExchangeLink(string league, string type)
        => $"{BaseUrl}/poe2/api/economy/exchange/current/overview?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(type)}";

    private static string GetStashLink(string league, string type)
        => $"{BaseUrl}/poe2/api/economy/stash/current/item/overview?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(type)}";

    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _reloadGate = new();
    private int _updating;
    private bool _disposed;
    private string _queuedLeague;
    private bool _queuedForceRefresh;
    private CollectiveApiData _collectedData;
    private string _loadedLeague;
    private string _requestedLeague;

    private sealed class LeagueMetadata
    {
        public DateTime LastLoadTime { get; set; }
    }

    public Action<string> log { get; set; }
    public NinjaPricerSettings Settings { get; set; }
    public string DataDirectory { get; set; }
    public CollectiveApiData CollectedData => Volatile.Read(ref _collectedData);

    // PoE2 category names are deliberately kept separate from PoE1.  They mirror the current
    // poe.ninja PoE2 API types only; the PoE1 plugin is used as a transport/lifecycle reference.
    private static readonly IReadOnlyDictionary<string, string> ExchangeCategoryMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Currency"] = "Currency",
            ["Breach"] = "Breach",
            ["Delirium"] = "Delirium",
            ["Essences"] = "Essences",
            ["Runes"] = "Runes",
            ["Ritual"] = "Ritual",
            ["Fragments"] = "Fragments",
            ["UncutGems"] = "UncutGems",
            ["Abyss"] = "Abyss",
            ["Expedition"] = "Expedition",
            ["Verisium"] = "Verisium",
            ["LineageSupportGems"] = "LineageSupportGems",
            ["SoulCores"] = "SoulCores",
            ["Idols"] = "Idols",
        };

    private static readonly IReadOnlyDictionary<string, string> StashCategoryMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Weapons"] = "UniqueWeapons",
            ["Armour"] = "UniqueArmours",
            ["Accessories"] = "UniqueAccessories",
            ["Flasks"] = "UniqueFlasks",
            ["Jewels"] = "UniqueJewels",
            // PoE2 0.5 retired UniqueMaps; map-like uniques are served as UniqueTablets.
            ["Tablets"] = "UniqueTablets",
            ["PrecursorTablets"] = "PrecursorTablets",
            ["Charms"] = "UniqueCharms",
            ["SanctumRelics"] = "UniqueSanctumRelics",
        };

    public void StartDataReload(string league, bool forceRefresh)
    {
        league = NormalizeLeague(league);
        lock (_reloadGate)
        {
            if (_disposed)
            {
                return;
            }

            _requestedLeague = league;
            if (!string.Equals(_loadedLeague, league, StringComparison.OrdinalIgnoreCase))
            {
                // Never expose the previous league while the new snapshot is loading.
                Volatile.Write(ref _collectedData, null);
                _loadedLeague = null;
            }

            if (Interlocked.CompareExchange(ref _updating, 1, 0) != 0)
            {
                _queuedLeague = league;
                _queuedForceRefresh |= forceRefresh;
                log?.Invoke("Update is already in progress; queued the latest league refresh");
                return;
            }
        }

        log?.Invoke($"Getting data for {league}");
        // Do not pass the lifetime token to Task.Run itself: if disposal wins the
        // scheduling race, a pre-cancelled task would skip ReloadDataAsync's finally
        // block and leave the single-flight gate stuck. The async body observes the
        // token and still exits promptly.
        _ = Task.Run(() => ReloadDataAsync(league, forceRefresh, _lifetime.Token));
    }

    private async Task ReloadDataAsync(string league, bool forceRefresh, CancellationToken cancellationToken)
    {
        try
        {
            log?.Invoke("Gathering data from poe.ninja");
            var cacheLeague = GetCacheLeagueName(league);
            var newData = new CollectiveApiData();
            var metadataPath = Path.Join(DataDirectory, cacheLeague, "meta.json");
            var tryWebFirst = forceRefresh;

            if (!tryWebFirst && Settings?.DataSourceSettings?.AutoReload == true)
            {
                tryWebFirst = await IsLocalCacheStale(metadataPath, cancellationToken).ConfigureAwait(false);
            }

            foreach (var (key, type) in ExchangeCategoryMap)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = $"{key}.json";
                var data = await LoadFromWebOrBackup<ExchangeOverview>(
                    fileName,
                    GetExchangeLink(league, type),
                    league,
                    tryWebFirst,
                    cancellationToken).ConfigureAwait(false);
                if (data != null)
                {
                    SetExchangeProperty(newData, key, data);
                }
            }

            foreach (var (key, type) in StashCategoryMap)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = $"{key}.json";
                var data = await LoadFromWebOrBackup<StashOverview>(
                    fileName,
                    GetStashLink(league, type),
                    league,
                    tryWebFirst,
                    cancellationToken).ConfigureAwait(false);
                if (data != null)
                {
                    SetStashProperty(newData, key, data);
                }
            }

            if (!HasUsableData(newData))
            {
                log?.Invoke("No usable PoE2 pricing data was loaded; keeping the previous snapshot");
                return;
            }

            lock (_reloadGate)
            {
                if (_disposed || !string.Equals(_requestedLeague, league, StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"Discarding superseded pricing snapshot for {league}");
                    return;
                }
            }

            // Keep the last known category for this league when one endpoint is temporarily
            // unavailable (429/5xx/maintenance). Never merge across leagues: a stale price from
            // another league is worse than an unavailable price.
            var previousData = Volatile.Read(ref _collectedData);
            if (previousData != null && string.Equals(_loadedLeague, league, StringComparison.OrdinalIgnoreCase))
            {
                newData.MergeMissingFrom(previousData);
                log?.Invoke("Merged unavailable categories from the previous same-league snapshot");
            }

            var derivedDivineRate = newData.DivineToExaltedRateRaw;
            if (derivedDivineRate > 0 && !double.IsNaN(derivedDivineRate) && !double.IsInfinity(derivedDivineRate))
                newData.DivineToExaltedRate = derivedDivineRate;
            await WriteTextAtomically(
                metadataPath,
                JsonConvert.SerializeObject(new LeagueMetadata { LastLoadTime = DateTime.UtcNow }),
                cancellationToken).ConfigureAwait(false);

            // Publish only after every category has had a chance to load.  Volatile publication
            // prevents readers on the render thread from observing a partially built snapshot.
            lock (_reloadGate)
            {
                if (_disposed || !string.Equals(_requestedLeague, league, StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"Discarding superseded pricing snapshot for {league}");
                    return;
                }

                Volatile.Write(ref _collectedData, newData);
                _loadedLeague = league;
            }
            log?.Invoke("Finished gathering data from poe.ninja; pricing snapshot updated");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log?.Invoke("Pricing data reload cancelled");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Pricing data reload failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _updating, 0);

            string nextLeague = null;
            var nextForceRefresh = false;
            lock (_reloadGate)
            {
                if (!_disposed && _queuedLeague != null)
                {
                    nextLeague = _queuedLeague;
                    nextForceRefresh = _queuedForceRefresh;
                    _queuedLeague = null;
                    _queuedForceRefresh = false;
                }
            }

            if (nextLeague != null)
            {
                StartDataReload(nextLeague, nextForceRefresh);
            }
        }
    }

    private void SetExchangeProperty(CollectiveApiData data, string name, ExchangeOverview value)
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

    private void SetStashProperty(CollectiveApiData data, string name, StashOverview value)
    {
        switch (name)
        {
            case "Weapons": data.Weapons = value; break;
            case "Armour": data.Armour = value; break;
            case "Accessories": data.Accessories = value; break;
            case "Flasks": data.Flasks = value; break;
            case "Jewels": data.Jewels = value; break;
            case "Tablets": data.Tablets = value; break;
            case "PrecursorTablets": data.PrecursorTablets = value; break;
            case "Charms": data.Charms = value; break;
            case "SanctumRelics": data.SanctumRelics = value; break;
        }
    }

    private async Task<bool> IsLocalCacheStale(string metadataPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(metadataPath))
        {
            return true;
        }

        try
        {
            var metadata = JsonConvert.DeserializeObject<LeagueMetadata>(
                await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false));
            if (metadata == null || metadata.LastLoadTime > DateTime.UtcNow)
            {
                return true;
            }

            var age = DateTime.UtcNow - metadata.LastLoadTime;
            return age > TimeSpan.FromMinutes(Settings?.DataSourceSettings?.ReloadPeriod?.Value ?? 15);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (Settings?.DebugSettings?.EnableDebugLogging == true)
            {
                log?.Invoke($"Metadata loading failed: {ex.Message}");
            }

            return true;
        }
    }

    private async Task<T> LoadFromWebOrBackup<T>(
        string fileName,
        string url,
        string league,
        bool tryWebFirst,
        CancellationToken cancellationToken) where T : class
    {
        var backupFile = Path.Join(DataDirectory, GetCacheLeagueName(league), fileName);

        if (tryWebFirst)
        {
            var webData = await LoadFromWeb<T>(fileName, url, backupFile, cancellationToken).ConfigureAwait(false);
            if (webData != null)
            {
                return webData;
            }
        }

        var backupData = await LoadFromBackup<T>(fileName, backupFile, cancellationToken).ConfigureAwait(false);
        if (backupData != null)
        {
            return backupData;
        }

        return tryWebFirst
            ? null
            : await LoadFromWeb<T>(fileName, url, backupFile, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> LoadFromWeb<T>(
        string fileName,
        string url,
        string backupFile,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            if (Settings?.DebugSettings?.EnableDebugLogging == true)
            {
                log?.Invoke($"Downloading {fileName}");
            }

            var cachedBody = File.Exists(backupFile)
                ? await File.ReadAllTextAsync(backupFile, cancellationToken).ConfigureAwait(false)
                : null;
            var result = await Utils.DownloadFromUrlWithStatus(url, cachedBody, cancellationToken)
                .ConfigureAwait(false);
            var data = JsonConvert.DeserializeObject<T>(result.Body);
            if (data == null)
            {
                throw new JsonException("The response was empty or could not be deserialized");
            }

            if (!result.NotModified)
            {
                await WriteTextAtomically(backupFile, result.Body, cancellationToken).ConfigureAwait(false);
            }

            if (Settings?.DebugSettings?.EnableDebugLogging == true)
            {
                log?.Invoke($"{fileName} downloaded{(result.NotModified ? " (304; cache is current)" : string.Empty)}");
            }

            return data;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (Settings?.DebugSettings?.EnableDebugLogging == true)
            {
                log?.Invoke($"{fileName} fresh data download failed: {ex.Message}");
            }

            return null;
        }
    }

    private async Task<T> LoadFromBackup<T>(
        string fileName,
        string backupFile,
        CancellationToken cancellationToken) where T : class
    {
        if (!File.Exists(backupFile))
        {
            if (Settings?.DebugSettings?.EnableDebugLogging == true)
            {
                log?.Invoke($"No backup for {fileName}");
            }

            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<T>(
                await File.ReadAllTextAsync(backupFile, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (Settings?.DebugSettings?.EnableDebugLogging == true)
            {
                log?.Invoke($"{fileName} backup data load failed: {ex.Message}");
            }

            return null;
        }
    }

    private static bool HasUsableData(CollectiveApiData data)
    {
        return data.Currency?.Lines?.Count > 0 ||
            data.Breach?.Lines?.Count > 0 ||
            data.Delirium?.Lines?.Count > 0 ||
            data.Essences?.Lines?.Count > 0 ||
            data.Runes?.Lines?.Count > 0 ||
            data.Ritual?.Lines?.Count > 0 ||
            data.Fragments?.Lines?.Count > 0 ||
            data.UncutGems?.Lines?.Count > 0 ||
            data.Abyss?.Lines?.Count > 0 ||
            data.Expedition?.Lines?.Count > 0 ||
            data.Verisium?.Lines?.Count > 0 ||
            data.LineageSupportGems?.Lines?.Count > 0 ||
            data.SoulCores?.Lines?.Count > 0 ||
            data.Idols?.Lines?.Count > 0 ||
            data.Weapons?.Lines?.Count > 0 ||
            data.Armour?.Lines?.Count > 0 ||
            data.Accessories?.Lines?.Count > 0 ||
            data.Flasks?.Lines?.Count > 0 ||
            data.Jewels?.Lines?.Count > 0 ||
            data.Tablets?.Lines?.Count > 0 ||
            data.PrecursorTablets?.Lines?.Count > 0 ||
            data.Charms?.Lines?.Count > 0 ||
            data.SanctumRelics?.Lines?.Count > 0;
    }

    private static string NormalizeLeague(string league)
        => string.IsNullOrWhiteSpace(league) ? "Standard" : league.Trim();

    private static string GetCacheLeagueName(string league)
    {
        var normalized = NormalizeLeague(league);
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(normalized
            .Select(character => character is '/' or '\\' || invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim('.', ' ');

        if (safe is "." or ".." || string.IsNullOrWhiteSpace(safe))
        {
            safe = "Standard";
        }

        return safe.Length <= MaxCacheLeagueLength ? safe : safe[..MaxCacheLeagueLength];
    }

    private static async Task WriteTextAtomically(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = new FileInfo(path).Directory;
        directory?.Create();
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // A failed cleanup must not hide the original download/write error.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_reloadGate)
        {
            _queuedLeague = null;
            _queuedForceRefresh = false;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

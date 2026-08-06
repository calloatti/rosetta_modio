using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RosettaModio
{
  public class CachedModEntry
  {
    public ulong FileId { get; set; }
    public long DateUpdated { get; set; }
    public bool Downloaded { get; set; } = false;
    public string Title { get; set; } = "";
    public string Creator { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
  }

  public class ModioCrawlerService
  {
    private string _gameId = string.Empty;
    private string _apiKey = string.Empty;
    private string _userId = string.Empty;
    private readonly string _baseDirectory;
    private readonly string _dataFolderPath;
    private readonly HttpClient _httpClient;

    // Hard limit: 2 GB in bytes (2,147,483,648 bytes)
    private const ulong MaxAllowedSizeBytes = 2L * 1024 * 1024 * 1024;

    public ModioCrawlerService()
    {
      _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
      _dataFolderPath = Path.Combine(_baseDirectory, "data_modio");
      Directory.CreateDirectory(_dataFolderPath);

      _httpClient = new HttpClient();
      _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
      _httpClient.DefaultRequestHeaders.Add("User-Agent", "TimberbornRosettaGenerator/1.0");
      _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public bool VerifyConfiguration()
    {
      string gameIdFile = Path.Combine(_baseDirectory, "modio_gameid.txt");
      string apiKeyFile = Path.Combine(_baseDirectory, "modio_apikey.txt");
      string userIdFile = Path.Combine(_baseDirectory, "modio_userid.txt");

      if (!File.Exists(gameIdFile) || !File.Exists(apiKeyFile) || !File.Exists(userIdFile))
      {
        LogService.Log("[Error] Missing configuration files. Ensure modio_gameid.txt, modio_apikey.txt, and modio_userid.txt exist.");
        return false;
      }

      _gameId = File.ReadAllText(gameIdFile).Trim();
      _apiKey = File.ReadAllText(apiKeyFile).Trim();
      _userId = File.ReadAllText(userIdFile).Trim();

      if (string.IsNullOrEmpty(_gameId) || string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_userId))
      {
        LogService.Log("[Error] Configuration files found, but one or more are empty.");
        return false;
      }

      return true;
    }

    private long GetMaxCachedDateUpdated()
    {
      if (!Directory.Exists(_dataFolderPath)) return 0;

      long maxDate = 0;
      int folderCount = 0;

      foreach (var folder in Directory.GetDirectories(_dataFolderPath))
      {
        string cacheFilePath = Path.Combine(folder, ".rosetta_cache.json");
        if (File.Exists(cacheFilePath))
        {
          try
          {
            string json = File.ReadAllText(cacheFilePath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("DateUpdated", out var dateProp))
            {
              long date = dateProp.GetInt64();
              if (date > maxDate) maxDate = date;
            }
            folderCount++;
          }
          catch { }
        }
      }

      LogService.Log($"[Cache] Scanned {folderCount} folder(s) for cache files. Max date_updated found: {maxDate} ({(maxDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(maxDate).ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : "N/A")})");
      return maxDate;
    }

    public void SavePendingCacheFile(ulong modId, ulong fileId, long dateUpdated, string title, string creator, string downloadUrl)
    {
      string modIdStr = modId.ToString();
      string folderPath = Path.Combine(_dataFolderPath, modIdStr);
      Directory.CreateDirectory(folderPath);

      var entry = new CachedModEntry
      {
        FileId = fileId,
        DateUpdated = dateUpdated,
        Downloaded = false,
        Title = title,
        Creator = creator,
        DownloadUrl = downloadUrl
      };

      try
      {
        string cacheFilePath = Path.Combine(folderPath, ".rosetta_cache.json");
        string json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(cacheFilePath, json);
      }
      catch (Exception ex)
      {
        LogService.Log($"[Cache Error] Failed to write cache for mod {modIdStr}: {ex.Message}");
      }
    }

    public async Task CrawlAndFlagModsAsync()
    {
      string sanitizedUserId = _userId.StartsWith("u-", StringComparison.OrdinalIgnoreCase)
        ? _userId
        : $"u-{_userId}";

      int limit = 100;
      int offset = 0;
      int totalFound = 0;
      int flaggedForDownload = 0;
      int skippedOversized = 0;

      long maxCachedDate = GetMaxCachedDateUpdated();
      string dateFilter = maxCachedDate > 0
        ? $"&date_updated-min={maxCachedDate}"
        : "";

      if (maxCachedDate > 0)
      {
        LogService.Log($"[Network] Requesting mods updated on/after timestamp {maxCachedDate} ({DateTimeOffset.FromUnixTimeSeconds(maxCachedDate):yyyy-MM-dd HH:mm:ss} UTC)...");
      }
      else
      {
        LogService.Log($"[Network] Crawling ALL mods for Game ID {_gameId} (First Run)...");
      }

      try
      {
        while (true)
        {
          string url = $"https://{sanitizedUserId}.modapi.io/v1/games/{_gameId}/mods?api_key={_apiKey}&tags=mod&_limit={limit}&_offset={offset}{dateFilter}";

          LogService.Log($"[Network] GET {SanitizeUrl(url)}");

          HttpResponseMessage? response = null;
          int retryCount = 0;
          const int maxRetries = 5;

          while (retryCount < maxRetries)
          {
            try
            {
              using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
              response = await _httpClient.GetAsync(url, cts.Token);

              if (response.StatusCode == (HttpStatusCode)429)
              {
                int delaySeconds = 60;

                if (response.Headers.RetryAfter != null && response.Headers.RetryAfter.Delta.HasValue)
                {
                  int headerSeconds = (int)response.Headers.RetryAfter.Delta.Value.TotalSeconds;
                  if (headerSeconds > 0) delaySeconds = headerSeconds;
                }

                LogService.Log($"[Rate Limit] HTTP 429 encountered. Backing off for {delaySeconds} second(s)...");
                await Task.Delay(delaySeconds * 1000);
                retryCount++;
                continue;
              }

              break;
            }
            catch (Exception reqEx)
            {
              LogService.Log($"[Error] GET request failed or timed out (Attempt {retryCount + 1}/{maxRetries}): {reqEx.Message}");
              retryCount++;
              if (retryCount >= maxRetries) break;
              await Task.Delay(2000);
            }
          }

          if (response == null || !response.IsSuccessStatusCode)
          {
            if (response != null)
            {
              string errorBody = await response.Content.ReadAsStringAsync();
              LogService.Log($"[Warning] Modio API rejected request: {response.StatusCode} ({(int)response.StatusCode})");
              LogService.Log($"[Warning] API Response Body: {errorBody}");
            }
            break;
          }

          string json = await response.Content.ReadAsStringAsync();
          using var document = JsonDocument.Parse(json);
          var dataElement = document.RootElement.GetProperty("data");

          if (dataElement.GetArrayLength() == 0) break;

          foreach (var mod in dataElement.EnumerateArray())
          {
            ulong modId = mod.GetProperty("id").GetUInt64();
            string modIdStr = modId.ToString();

            long dateUpdated = mod.TryGetProperty("date_updated", out var dateProp) ? dateProp.GetInt64() : 0;
            string title = mod.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";

            string creator = "";
            if (mod.TryGetProperty("submitted_by", out var userProp) && userProp.TryGetProperty("username", out var usernameProp))
            {
              creator = usernameProp.GetString() ?? "";
            }

            ulong latestFileId = 0;
            string downloadUrl = "";

            if (mod.TryGetProperty("modfile", out var modfileProp))
            {
              latestFileId = modfileProp.GetProperty("id").GetUInt64();

              // === SIZE FILTER CHECK (2 GB Limit) ===
              if (modfileProp.TryGetProperty("filesize", out var sizeProp))
              {
                ulong filesize = sizeProp.GetUInt64();
                if (filesize > MaxAllowedSizeBytes)
                {
                  double filesizeMB = filesize / (1024.0 * 1024.0);
                  LogService.Log($"[Skip] Mod {modIdStr} ('{title}') skipped - File size ({filesizeMB:F1} MB) exceeds 2 GB limit.");
                  skippedOversized++;
                  continue;
                }
              }

              if (modfileProp.TryGetProperty("download", out var downloadProp) && downloadProp.TryGetProperty("binary_url", out var binaryUrlProp))
              {
                downloadUrl = binaryUrlProp.GetString() ?? "";
              }
            }

            if (string.IsNullOrEmpty(downloadUrl)) continue;

            string cacheFilePath = Path.Combine(_dataFolderPath, modIdStr, ".rosetta_cache.json");
            bool needsDownload = true;

            if (File.Exists(cacheFilePath))
            {
              try
              {
                string localJson = File.ReadAllText(cacheFilePath);
                var existing = JsonSerializer.Deserialize<CachedModEntry>(localJson);
                if (existing != null && existing.FileId == latestFileId && existing.Downloaded)
                {
                  needsDownload = false;
                }
              }
              catch { }
            }

            if (needsDownload)
            {
              SavePendingCacheFile(modId, latestFileId, dateUpdated, title, creator, downloadUrl);
              flaggedForDownload++;
            }
          }

          int returnedCount = dataElement.GetArrayLength();
          totalFound += returnedCount;
          LogService.Log($"[Network] Crawled offset {offset}: {returnedCount} mods processed.");

          if (returnedCount < limit) break;
          offset += limit;

          await Task.Delay(1000);
        }

        LogService.Log($"[Summary] Crawled {totalFound} total items matching tag 'mod'. Skipped {skippedOversized} oversized item(s) (>2GB). {flaggedForDownload} flagged for download.");
      }
      catch (Exception ex)
      {
        LogService.Log($"[Fatal Error] Modio crawl failed: {ex.Message}");
      }
    }

    private string SanitizeUrl(string url)
    {
      if (string.IsNullOrEmpty(url)) return "";
      return Regex.Replace(url, @"([?&]api_key=)[^&]+", "$1***REDACTED***", RegexOptions.IgnoreCase);
    }
  }
}
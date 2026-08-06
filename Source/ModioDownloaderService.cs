using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RosettaModio
{
  public class ModioDownloaderService
  {
    private const int WorkerCount = 4;
    private const int MaxAttempts = 5;

    // Hard limit: 2 GB in bytes (2,147,483,648 bytes)
    private const long MaxAllowedSizeBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _targetBaseFolder;
    private readonly HttpClient _httpClient;
    private int _totalMods;
    private int _completed;
    private int _failedDownloads;

    public ModioDownloaderService()
    {
      string rootPath = AppDomain.CurrentDomain.BaseDirectory;
      _targetBaseFolder = Path.Combine(rootPath, "data_modio");
      Directory.CreateDirectory(_targetBaseFolder);

      _httpClient = new HttpClient();
      _httpClient.DefaultRequestHeaders.Add("User-Agent", "TimberbornRosettaGenerator/1.0");
      _httpClient.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task ProcessPendingDownloadsAsync()
    {
      if (!Directory.Exists(_targetBaseFolder)) return;

      var pendingMods = new List<(string ModId, string DownloadUrl)>();

      foreach (var folderPath in Directory.GetDirectories(_targetBaseFolder))
      {
        string modId = Path.GetFileName(folderPath);
        string cachePath = Path.Combine(folderPath, ".rosetta_cache.json");

        if (File.Exists(cachePath))
        {
          try
          {
            string json = File.ReadAllText(cachePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool downloaded = root.TryGetProperty("Downloaded", out var dlProp) && dlProp.GetBoolean();
            string downloadUrl = root.TryGetProperty("DownloadUrl", out var urlProp) ? urlProp.GetString() ?? "" : "";

            if (!downloaded && !string.IsNullOrEmpty(downloadUrl))
            {
              pendingMods.Add((modId, downloadUrl));
            }
          }
          catch { }
        }
      }

      if (pendingMods.Count == 0)
      {
        LogService.Log("\n[Notice] No pending mod downloads found on disk.");
        return;
      }

      LogService.Log($"\n=== STARTING MANIFEST DOWNLOAD PIPELINE ({pendingMods.Count} pending) ===");

      _totalMods = pendingMods.Count;
      _completed = 0;
      _failedDownloads = 0;

      var queue = new ConcurrentQueue<(string ModId, string DownloadUrl)>(pendingMods);
      var workers = Enumerable.Range(0, WorkerCount).Select(i => RunWorkerAsync(i, queue)).ToArray();
      await Task.WhenAll(workers);

      LogService.Log($"[Pipeline] Completed {_completed}/{_totalMods} downloads.");

      if (_failedDownloads > 0)
      {
        LogService.Log($"[Warning] {_failedDownloads} mods failed to download.");
      }
    }

    private async Task RunWorkerAsync(int workerIndex, ConcurrentQueue<(string ModId, string DownloadUrl)> queue)
    {
      while (queue.TryDequeue(out var item))
      {
        int idx = Interlocked.Increment(ref _completed);
        await ProcessModAsync(workerIndex, item.ModId, item.DownloadUrl, idx, _totalMods);
      }
    }

    private async Task<bool> ProcessModAsync(int workerIndex, string modId, string downloadUrl, int idx, int total)
    {
      string localDataPath = Path.Combine(_targetBaseFolder, modId);
      string tempZipPath = Path.Combine(localDataPath, $"{modId}_temp.zip");

      for (int attempt = 1; attempt <= MaxAttempts; attempt++)
      {
        try
        {
          LogService.Log($"[{idx}/{total}] [{workerIndex}] Downloading {modId} (Attempt {attempt}/{MaxAttempts})...");

          using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
          using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);

          if (response.StatusCode == (HttpStatusCode)429)
          {
            int delaySeconds = 60;
            if (response.Headers.RetryAfter != null && response.Headers.RetryAfter.Delta.HasValue)
            {
              int headerSeconds = (int)response.Headers.RetryAfter.Delta.Value.TotalSeconds;
              if (headerSeconds > 0) delaySeconds = headerSeconds;
            }

            LogService.Log($"[{idx}/{total}] [{workerIndex}] [Rate Limit] HTTP 429 on download for {modId}. Waiting {delaySeconds} second(s)...");
            await Task.Delay(delaySeconds * 1000);
            continue;
          }

          response.EnsureSuccessStatusCode();

          // Check Content-Length from response headers
          long contentLength = response.Content.Headers.ContentLength ?? 0;
          if (contentLength > MaxAllowedSizeBytes)
          {
            double sizeMB = contentLength / (1024.0 * 1024.0);
            LogService.Log($"[{idx}/{total}] [{workerIndex}] [Skip] {modId} skipped - Size ({sizeMB:F1} MB) exceeds 2 GB limit.");

            // Mark as downloaded in cache so it stops re-attempting on future runs
            MarkCacheAsDownloaded(localDataPath);
            return false;
          }

          // Stream directly to disk instead of memory to prevent "Stream was too long" exceptions
          Directory.CreateDirectory(localDataPath);
          using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
          {
            await response.Content.CopyToAsync(fs, cts.Token);

            // Verify actual written disk size in case Content-Length header was missing
            if (fs.Length > MaxAllowedSizeBytes)
            {
              double writtenMB = fs.Length / (1024.0 * 1024.0);
              LogService.Log($"[{idx}/{total}] [{workerIndex}] [Skip] {modId} downloaded stream ({writtenMB:F1} MB) exceeded 2 GB limit.");

              fs.Close();
              if (File.Exists(tempZipPath)) File.Delete(tempZipPath);

              MarkCacheAsDownloaded(localDataPath);
              return false;
            }
          }

          CleanFolderExceptCache(localDataPath, tempZipPath);

          int manifestsFound = 0;
          using (var archive = ZipFile.OpenRead(tempZipPath))
          {
            foreach (var entry in archive.Entries)
            {
              if (entry.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
              {
                string destinationPath = Path.Combine(localDataPath, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                entry.ExtractToFile(destinationPath, true);
                manifestsFound++;
              }
            }
          }

          // Delete temporary zip file after extraction
          if (File.Exists(tempZipPath)) File.Delete(tempZipPath);

          MarkCacheAsDownloaded(localDataPath);
          LogService.Log($"[{idx}/{total}] OK {modId} (Extracted {manifestsFound} manifest(s))");
          return true;
        }
        catch (Exception ex)
        {
          if (File.Exists(tempZipPath)) try { File.Delete(tempZipPath); } catch { }

          LogService.Log($"[{idx}/{total}] Attempt {attempt}/{MaxAttempts} failed for {modId}: {ex.Message}");
          if (attempt == MaxAttempts)
          {
            Interlocked.Increment(ref _failedDownloads);
            LogService.Log($"[Warning] {modId} could not be downloaded after {MaxAttempts} attempts.");
          }
        }
        await Task.Delay(attempt * 1000);
      }
      return false;
    }

    private void CleanFolderExceptCache(string folderPath, string keepFile)
    {
      if (!Directory.Exists(folderPath)) return;

      var dirInfo = new DirectoryInfo(folderPath);

      foreach (var file in dirInfo.GetFiles())
      {
        if (file.FullName.Equals(keepFile, StringComparison.OrdinalIgnoreCase)) continue;
        if (!file.Name.Equals(".rosetta_cache.json", StringComparison.OrdinalIgnoreCase))
        {
          file.Delete();
        }
      }

      foreach (var dir in dirInfo.GetDirectories())
      {
        dir.Delete(true);
      }
    }

    private void MarkCacheAsDownloaded(string folderPath)
    {
      string cachePath = Path.Combine(folderPath, ".rosetta_cache.json");
      if (!File.Exists(cachePath)) return;

      try
      {
        string json = File.ReadAllText(cachePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var updated = new CachedModEntry
        {
          FileId = root.GetProperty("FileId").GetUInt64(),
          DateUpdated = root.GetProperty("DateUpdated").GetInt64(),
          Downloaded = true,
          Title = root.TryGetProperty("Title", out var tProp) ? tProp.GetString() ?? "" : "",
          Creator = root.TryGetProperty("Creator", out var cProp) ? cProp.GetString() ?? "" : "",
          DownloadUrl = root.TryGetProperty("DownloadUrl", out var uProp) ? uProp.GetString() ?? "" : ""
        };

        string updatedJson = JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(cachePath, updatedJson);
      }
      catch (Exception ex)
      {
        LogService.Log($"[Cache Error] Failed to set Downloaded=true for {folderPath}: {ex.Message}");
      }
    }
  }
}
using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace RosettaModio
{
  public class ModDependency
  {
    public string Id { get; set; } = "";
    public string MinimumVersion { get; set; } = "";
  }

  public class RosettaData
  {
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string MinimumGameVersion { get; set; } = "";
    public List<ModDependency> RequiredMods { get; set; } = new();
    public List<ModDependency> OptionalMods { get; set; } = new();
  }

  public class ModioManifestProcessorService
  {
    private readonly string _targetBaseFolder;
    private readonly string _rosettaTxtPath;
    private readonly string _duplicatesTxtPath;
    private readonly string _invalidPackagesTxtPath;

    public ModioManifestProcessorService()
    {
      string rootPath = AppDomain.CurrentDomain.BaseDirectory;
      _targetBaseFolder = Path.Combine(rootPath, "data_modio");
      _rosettaTxtPath = Path.Combine(rootPath, "rosetta_modio.txt");
      _duplicatesTxtPath = Path.Combine(rootPath, "rosetta_duplicates_modio.txt");
      _invalidPackagesTxtPath = Path.Combine(rootPath, "rosetta_invalid_packages_modio.txt");
    }

    public void RunExport()
    {
      LogService.Log($"=== STARTING ROSETTA EXPORT ({Path.GetFileName(_rosettaTxtPath)}) ===");

      if (!Directory.Exists(_targetBaseFolder))
      {
        LogService.Log("[Error] No downloaded mods found in data_modio.");
        return;
      }

      var directories = Directory.GetDirectories(_targetBaseFolder)
          .OrderBy(d => ulong.TryParse(Path.GetFileName(d), out ulong id) ? id : 0);

      var sb = new StringBuilder();
      sb.AppendLine("PublishedFileID\tId\tDirectory_Name\tName\tTitle\tVersion\tMinimumGameVersion\tRequiredMods_Id\tRequiredMods_MinimumVersion\tOptionalMods_Id\tOptionalMods_MinimumVersion");

      var uniqueIdMappings = new HashSet<(string PublishedFileId, string Id, string Creator)>();
      int processed = 0;

      foreach (var modFolder in directories)
      {
        string publishedFileId = Sanitize(new DirectoryInfo(modFolder).Name);

        var allManifests = Directory.GetFiles(modFolder, "manifest.json", SearchOption.AllDirectories);
        if (allManifests.Length == 0) continue;

        // Strip single wrapper directory if present
        string effectiveRootFolder = modFolder;
        var subDirs = Directory.GetDirectories(modFolder);
        var rootFiles = Directory.GetFiles(modFolder).Where(f => !Path.GetFileName(f).Equals(".rosetta_cache.json", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (subDirs.Length == 1 && rootFiles.Length == 0)
        {
          effectiveRootFolder = subDirs[0];
        }

        string rootManifestPath = Path.Combine(effectiveRootFolder, "manifest.json");
        bool hasSubfolderManifests = allManifests.Any(p => !p.Equals(rootManifestPath, StringComparison.OrdinalIgnoreCase));

        // Read Title and Creator directly from local .rosetta_cache.json
        string workshopTitle = "";
        string workshopCreator = "";
        string localCachePath = Path.Combine(modFolder, ".rosetta_cache.json");

        if (File.Exists(localCachePath))
        {
          try
          {
            string json = File.ReadAllText(localCachePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("Title", out var titleProp)) workshopTitle = Sanitize(titleProp.GetString() ?? "");
            if (root.TryGetProperty("Creator", out var creatorProp)) workshopCreator = Sanitize(creatorProp.GetString() ?? "");
          }
          catch { }
        }

        foreach (var manifestPath in allManifests)
        {
          try
          {
            if (hasSubfolderManifests && manifestPath.Equals(rootManifestPath, StringComparison.OrdinalIgnoreCase))
            {
              continue;
            }

            var data = ParseManifest(manifestPath);
            if (data == null) continue;

            if (!string.IsNullOrWhiteSpace(data.Id))
            {
              uniqueIdMappings.Add((publishedFileId, data.Id, workshopCreator));
            }

            string manifestDir = Path.GetDirectoryName(manifestPath)!;
            string relativePath = Path.GetRelativePath(effectiveRootFolder, manifestDir);
            string directoryName = Sanitize(relativePath == "." ? "" : relativePath);

            string baseData = $"{publishedFileId}\t{data.Id}\t{directoryName}\t{data.Name}\t{workshopTitle}\t{data.Version}\t{data.MinimumGameVersion}";

            bool hasDependencies = data.RequiredMods.Count > 0 || data.OptionalMods.Count > 0;

            if (!hasDependencies)
            {
              sb.AppendLine($"{baseData}\t\t\t\t");
            }
            else
            {
              foreach (var req in data.RequiredMods)
              {
                sb.AppendLine($"{baseData}\t{req.Id}\t{req.MinimumVersion}\t\t");
              }

              foreach (var opt in data.OptionalMods)
              {
                sb.AppendLine($"{baseData}\t\t\t{opt.Id}\t{opt.MinimumVersion}");
              }
            }

            processed++;
          }
          catch (Exception ex)
          {
            LogService.Log($"[Warning] Skipped manifest {manifestPath}: {ex.Message}");
          }
        }
      }

      File.WriteAllText(_rosettaTxtPath, sb.ToString());
      LogService.Log($"[Export] {_rosettaTxtPath} created successfully. Processed {processed} manifests.");

      ExportIdAnalysis(uniqueIdMappings);
      LogService.Log("=== ROSETTA EXPORT COMPLETE ===");
    }

    private string Sanitize(string input)
    {
      if (string.IsNullOrEmpty(input)) return "";
      string result = Regex.Replace(input, @"[\t\r\n\v]+", " ");
      result = Regex.Replace(result, @"\s+", " ");
      return result.Trim();
    }

    private void ExportIdAnalysis(HashSet<(string PublishedFileId, string Id, string Creator)> uniquePairs)
    {
      var dupSb = new StringBuilder();
      dupSb.AppendLine("PublishedFileID\tId\tCreator\tTitle");

      var conflictingIds = uniquePairs
          .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
          .Where(g => g.Select(p => p.PublishedFileId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
          .Select(g => g.Key);

      int crossConflicts = 0;
      foreach (var id in conflictingIds.OrderBy(x => x))
      {
        var associatedRecords = uniquePairs
            .Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.PublishedFileId);

        foreach (var record in associatedRecords)
        {
          string title = GetModTitleFromFolder(record.PublishedFileId);
          dupSb.AppendLine($"{record.PublishedFileId}\t{record.Id}\t{record.Creator}\t{title}");
        }
        crossConflicts++;
      }
      File.WriteAllText(_duplicatesTxtPath, dupSb.ToString());

      var pkgSb = new StringBuilder();
      pkgSb.AppendLine("PublishedFileID\tId\tCreator\tTitle");

      var ruleBreakers = uniquePairs
          .GroupBy(p => p.PublishedFileId, StringComparer.OrdinalIgnoreCase)
          .Where(g => g.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
          .Select(g => g.Key);

      int invalidPackages = 0;
      foreach (var pubId in ruleBreakers.OrderBy(x => x))
      {
        var associatedRecords = uniquePairs
            .Where(p => p.PublishedFileId.Equals(pubId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Id);

        string title = GetModTitleFromFolder(pubId);

        foreach (var record in associatedRecords)
        {
          pkgSb.AppendLine($"{record.PublishedFileId}\t{record.Id}\t{record.Creator}\t{title}");
        }
        invalidPackages++;
      }
      File.WriteAllText(_invalidPackagesTxtPath, pkgSb.ToString());

      if (crossConflicts > 0) LogService.Log($"[Analysis] Warning: {crossConflicts} internal Mod IDs conflict across separate mod.io items. Details in {_duplicatesTxtPath}");
      if (invalidPackages > 0) LogService.Log($"[Analysis] Alert: {invalidPackages} mod.io items illegally use multiple separate Mod IDs across folders. Details in {_invalidPackagesTxtPath}");
    }

    private string GetModTitleFromFolder(string publishedFileId)
    {
      string localCachePath = Path.Combine(_targetBaseFolder, publishedFileId, ".rosetta_cache.json");
      if (File.Exists(localCachePath))
      {
        try
        {
          string json = File.ReadAllText(localCachePath);
          using var doc = JsonDocument.Parse(json);
          if (doc.RootElement.TryGetProperty("Title", out var titleProp))
          {
            return Sanitize(titleProp.GetString() ?? "");
          }
        }
        catch { }
      }
      return "";
    }

    private RosettaData? ParseManifest(string path)
    {
      string json;
      try
      {
        json = File.ReadAllText(path, Encoding.UTF8);
      }
      catch (Exception ex)
      {
        LogService.Log($"[Warning] Failed to read manifest {path}: {ex.Message}");
        return null;
      }

      try
      {
        var options = new JsonSerializerOptions
        {
          PropertyNameCaseInsensitive = true,
          AllowTrailingCommas = true,
          ReadCommentHandling = JsonCommentHandling.Skip
        };

        var data = JsonSerializer.Deserialize<RosettaData>(json, options) ?? new RosettaData();

        return new RosettaData
        {
          Id = Sanitize(data.Id),
          Name = Sanitize(data.Name),
          Version = Sanitize(data.Version),
          MinimumGameVersion = Sanitize(data.MinimumGameVersion),
          RequiredMods = (data.RequiredMods ?? new()).Select(r => new ModDependency { Id = Sanitize(r.Id), MinimumVersion = Sanitize(r.MinimumVersion) }).ToList(),
          OptionalMods = (data.OptionalMods ?? new()).Select(o => new ModDependency { Id = Sanitize(o.Id), MinimumVersion = Sanitize(o.MinimumVersion) }).ToList()
        };
      }
      catch
      {
        return new RosettaData
        {
          Id = Sanitize(ExtractValueRegex(json, "Id")),
          Name = Sanitize(ExtractValueRegex(json, "Name")),
          Version = Sanitize(ExtractValueRegex(json, "Version")),
          MinimumGameVersion = Sanitize(ExtractValueRegex(json, "MinimumGameVersion")),
          RequiredMods = new List<ModDependency>(),
          OptionalMods = new List<ModDependency>()
        };
      }
    }

    private string ExtractValueRegex(string json, string key)
    {
      var match = Regex.Match(
          json,
          $@"[""']?{key}[""']?\s*:\s*(?:[""']([^""'\r\n]+)[""']|([^\s,\r\n}}]+))",
          RegexOptions.IgnoreCase);

      if (match.Success)
      {
        if (!string.IsNullOrWhiteSpace(match.Groups[1].Value)) return match.Groups[1].Value;
        if (!string.IsNullOrWhiteSpace(match.Groups[2].Value)) return match.Groups[2].Value;
      }
      return "";
    }
  }
}
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RosettaModio
{
  class Program
  {
    static async Task Main(string[] args)
    {
      LogService.Log("=== ROSETTA Modio PIPELINE STARTING ===");

      try
      {
        string dataFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data_modio");

        // Check for -clean or --clean command line argument
        if (args.Any(a => a.Equals("-clean", StringComparison.OrdinalIgnoreCase) || a.Equals("--clean", StringComparison.OrdinalIgnoreCase)))
        {
          LogService.Log("[Clean] -clean flag detected. Wiping local data_modio directory...");
          if (Directory.Exists(dataFolderPath))
          {
            Directory.Delete(dataFolderPath, true);
            LogService.Log("[Clean] data_modio deleted successfully.");
          }
        }

        // Phase 1 & 2: Verify config, scan disk for max date, crawl mod.io, flag pending mods
        var crawler = new ModioCrawlerService();
        if (!crawler.VerifyConfiguration())
        {
          LogService.Log("[Notice] Setup failed due to missing configuration.");
          Environment.ExitCode = 1;
          return;
        }

        await crawler.CrawlAndFlagModsAsync();

        // Phase 3: Independent download pass scanning disk for Downloaded=false
        var downloader = new ModioDownloaderService();
        await downloader.ProcessPendingDownloadsAsync();

        // Phase 4: Manifest export reading manifests and .rosetta_cache.json directly from folders
        var processor = new ModioManifestProcessorService();
        processor.RunExport();
      }
      catch (Exception ex)
      {
        LogService.Log($"\n[Fatal Error] Application execution crashed: {ex.Message}");
        Environment.ExitCode = 1;
      }
      finally
      {
        LogService.Log("\n=== ROSETTA Modio PIPELINE FINISHED ===");
      }
    }
  }
}
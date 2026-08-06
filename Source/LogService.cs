using System;
using System.IO;

namespace RosettaModio
{
  public static class LogService
  {
    private static readonly string _logFilePath;
    private static readonly object _fileLock = new();

    static LogService()
    {
      string exePath = AppDomain.CurrentDomain.BaseDirectory;
      _logFilePath = Path.Combine(exePath, "rosetta_modio.log");

      try
      {
        if (File.Exists(_logFilePath))
        {
          File.Delete(_logFilePath);
        }
      }
      catch { }
    }

    public static void Log(string message)
    {
      string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

      Console.WriteLine(logLine);

      try
      {
        lock (_fileLock)
        {
          File.AppendAllText(_logFilePath, logLine + Environment.NewLine);
        }
      }
      catch { }
    }
  }
}
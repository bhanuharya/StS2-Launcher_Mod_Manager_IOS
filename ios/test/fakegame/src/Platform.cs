using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MegaCrit.Sts2.Core.Debug
{
    public static class OsDebugInfo
    {
        public static void LogSystemInfo() => throw new InvalidOperationException("desktop-only system info");
    }

    public static class SentryService
    {
        public static void Initialize() => throw new InvalidOperationException("Sentry has no iOS build");
    }
}

namespace MegaCrit.Sts2.Core.Saves
{
    public class PrefsSave
    {
        public bool UploadData => true;
    }

    public class GodotFileIo
    {
        public List<string> Created { get; } = new();

        public string GetFullPath(string path) => path == "." ? "." : "user://" + path;

        public void CreateDirectory(string directoryPath) => Created.Add(GetFullPath(directoryPath));
    }
}

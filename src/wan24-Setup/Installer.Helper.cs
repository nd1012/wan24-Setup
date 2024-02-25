using System.Reflection;
using System.Runtime.CompilerServices;
using wan24.Core;

namespace wan24.Setup
{
    // Helper
    public static partial class Installer
    {
        /// <summary>
        /// Copy all temporary files and folders from the working folder to the app path during setup (excluding setup and configuration)
        /// </summary>
        /// <param name="exclude">A list of relative file- and foldernames to exclude in addition</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Target files and folders</returns>
        public static async IAsyncEnumerable<string> CopyFilesAsync(string[] exclude, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Arguments is null) throw new InvalidOperationException("Setup is not running");
            string tempFolder = $"{Path.GetFullPath("./")}",
                setupFileName = Path.GetFileName(Assembly.GetEntryAssembly()!.Location);
            FileStreamOptions fso = new()
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None
            };
#pragma warning disable CA1416 // Not available on all platforms
            if (ENV.IsLinux) fso.UnixCreateMode = Settings.CreateFileMode;
#pragma warning restore CA1416 // Not available on all platforms
            foreach (string fn in Directory.GetFileSystemEntries(tempFolder, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = fn[tempFolder.Length..],
                    fileName = Path.GetFileName(name);
                if (exclude.Contains(name) || fileName == SETUP_CONFIG_FILENAME || fileName == setupFileName) continue;
                string targetName = Path.Combine(AppPath!, name);
                Logging.WriteInfo($"Copy {fn} to {targetName}");
                if (File.Exists(fn))
                {
                    if (File.Exists(targetName)) Logging.WriteInfo($"Overwriting existing file \"{targetName}\"");
                    FileStream source = new(fn, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await using (source.DynamicContext())
                    {
                        FileStream target = new(targetName, fso);
                        await using (target.DynamicContext())
                        {
                            if (target.Length != 0) target.SetLength(0);
                            await source.CopyToAsync(target, cancellationToken).DynamicContext();
                        }
                    }
                }
                else if (!Directory.Exists(targetName))
                {
                    if (ENV.IsLinux)
                    {
#pragma warning disable CA1416 // Not available on all platforms
                        Directory.CreateDirectory(targetName, Settings.CreateFolderMode);
#pragma warning restore CA1416 // Not available on all platforms
                    }
                    else
                    {
                        Directory.CreateDirectory(targetName);
                    }
                }
                yield return targetName;
            }
        }
    }
}

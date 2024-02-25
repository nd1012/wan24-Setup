using System.IO.Compression;
using wan24.Compression;
using wan24.Core;
using wan24.StreamSerializerExtensions;

namespace wan24.Setup
{
    // Package
    public static partial class Installer
    {
        /// <summary>
        /// Create an installer package
        /// </summary>
        /// <param name="file">Filename (will be overwritten, if exists)</param>
        /// <param name="basePath">Base path to strip off the filenames</param>
        /// <param name="files">Files to include (may contain folder names also)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Uncompressed size in bytes</returns>
        public static async Task<long> CreateInstallerPackageAsync(string file, string basePath, IEnumerable<string> files, CancellationToken cancellationToken = default)
        {
            basePath = $"{Path.GetFullPath(basePath)}/";
            // Create a temporary installer package
            string tempFile = Path.Combine(Settings.TempFolder, Guid.NewGuid().ToString());
            FileStreamOptions fso = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None
            };
#pragma warning disable CA1416 // Not available on all platforms
            if (ENV.IsLinux) fso.UnixCreateMode = Settings.CreateFileMode;
#pragma warning restore CA1416 // Not available on all platforms
            FileStream tempTarget = new(tempFile, fso);
            try
            {
                long res;
                await using (tempTarget.DynamicContext())
                {
                    await tempTarget.WriteSerializerVersionAsync(cancellationToken).DynamicContext();
                    foreach (string fn in files)
                    {
                        if (!fn.StartsWith(basePath)) throw new ArgumentException($"Filename \"{fn}\" doesn't start with the base path \"{basePath}\"", nameof(files));
                        await tempTarget.WriteStringNullableAsync(fn[basePath.Length..], cancellationToken).DynamicContext();
                        if (File.Exists(fn))
                        {
                            await tempTarget.WriteEnumAsync(InstallerPackageItemTypes.File, cancellationToken).DynamicContext();
                            FileStream source = new(fn, FileMode.Open, FileAccess.Read, FileShare.Read);
                            await using (source.DynamicContext())
                            {
                                await tempTarget.WriteNumberAsync(source.Length, cancellationToken).DynamicContext();
                                await source.CopyToAsync(tempTarget, cancellationToken).DynamicContext();
                            }
                        }
                        else if (Directory.Exists(fn))
                        {
                            await tempTarget.WriteEnumAsync(InstallerPackageItemTypes.Folder, cancellationToken).DynamicContext();
                        }
                        else
                        {
                            throw new FileNotFoundException("Source file not found", fn);
                        }
                    }
                    await tempTarget.WriteStringNullableAsync(value: null, cancellationToken).DynamicContext();
                    await tempTarget.FlushAsync(cancellationToken).DynamicContext();
                    res = tempTarget.Length;
                    tempTarget.Position = 0;
                    // Compress the temporary installer package to the target file
                    fso.Mode = FileMode.OpenOrCreate;
                    FileStream target = new(file, fso);
                    await using (target.DynamicContext())
                    {
                        if (target.Length != 0) target.SetLength(0);
                        CompressionOptions options = BrotliCompressionAlgorithm.Instance.DefaultOptions;
                        options.SerializerVersionIncluded = true;
                        options.AlgorithmIncluded = false;
                        options.FlagsIncluded = true;
                        options.UncompressedDataLength = res;
                        options.UncompressedLengthIncluded = true;
                        options.Level = CompressionLevel.Optimal;
                        options.LeaveOpen = true;
                        await tempTarget.CompressAsync(target, options, cancellationToken).DynamicContext();
                    }
                }
                // Delete the temporary installer package
                File.Delete(tempFile);
                return res;
            }
            catch
            {
                if (File.Exists(tempFile))
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch (Exception ex)
                    {
                        Logging.WriteError($"Failed to delete temporary setup file \"{tempFile}\" after error: {ex}");
                    }
                if (File.Exists(file))
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        Logging.WriteError($"Failed to delete setup file \"{file}\" after error: {ex}");
                    }
                throw;
            }
        }

        /// <summary>
        /// Extract an installer package
        /// </summary>
        /// <param name="source">Source stream</param>
        /// <param name="folder">Target folder (ends with a slash)</param>
        /// <param name="compression">Compression options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public static async Task ExtractInstallerPackageAsync(Stream source, string folder, CompressionOptions? compression = null, CancellationToken cancellationToken = default)
        {
            InstallerConfig config = new()
            {
                Source = source,
                TempPath = folder,
                Cancellation = cancellationToken
            };
            if (compression is not null) config.Compression = compression;
            await ExtractInstallerPackageAsync(config).DynamicContext();
        }

        /// <summary>
        /// Extract an installer package
        /// </summary>
        /// <param name="config">Installer configuration</param>
        private static async Task ExtractInstallerPackageAsync(InstallerConfig config)
        {
            config.TempPath = $"{Path.GetFullPath(config.TempPath)}/";
            config.Compression = await BrotliCompressionAlgorithm.Instance.ReadOptionsAsync(config.Source, Stream.Null, config.Compression, config.Cancellation).DynamicContext();
            Stream uncompressedSource = BrotliCompressionAlgorithm.Instance.GetDecompressionStream(config.Source, config.Compression);
            config.UncompressedSource = uncompressedSource;
            Logging.WriteInfo($"Extracting installer files and folders to \"{config.TempPath}\"");
            await using (uncompressedSource.DynamicContext())
            {
                int ssv = await uncompressedSource.ReadSerializerVersionAsync(config.Cancellation).DynamicContext();
                FileStreamOptions fso = new()
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None
                };
#pragma warning disable CA1416 // Not available on all platforms
                if (ENV.IsLinux) fso.UnixCreateMode = Settings.CreateFileMode;
#pragma warning restore CA1416 // Not available on all platforms
                while (await uncompressedSource.ReadStringNullableAsync(ssv, maxLen: short.MaxValue, cancellationToken: config.Cancellation).DynamicContext() is string fn)
                {
                    InstallerPackageItemTypes type = await uncompressedSource.ReadEnumAsync<InstallerPackageItemTypes>(ssv, cancellationToken: config.Cancellation).DynamicContext();
                    fn = Path.GetFullPath(Path.Combine(config.TempPath, fn));
                    if (!fn.StartsWith(config.TempPath)) throw new InvalidDataException($"Invalid item name \"{fn}\"");
                    switch (type)
                    {
                        case InstallerPackageItemTypes.File:
                            long len = await uncompressedSource.ReadNumberAsync<long>(ssv, cancellationToken: config.Cancellation).DynamicContext();
                            if (len < 0) throw new InvalidDataException($"Invalid file length {len}");
                            Logging.WriteInfo($"Writing setup file \"{fn}\" with {len} bytes");
                            FileStream target = new(fn, fso);
                            await using (target.DynamicContext())
                            {
                                if (target.Length != 0) target.SetLength(0);
                                await uncompressedSource.CopyExactlyPartialToAsync(target, len, cancellationToken: config.Cancellation).DynamicContext();
                            }
                            break;
                        case InstallerPackageItemTypes.Folder:
                            if (!Directory.Exists(fn))
                            {
                                Logging.WriteInfo($"Creating setup folder \"{fn}\"");
                                if (ENV.IsLinux)
                                {
#pragma warning disable CA1416 // Not available on all platforms
                                    Directory.CreateDirectory(fn, Settings.CreateFolderMode);
#pragma warning restore CA1416 // Not available on all platforms
                                }
                                else
                                {
                                    Directory.CreateDirectory(fn);
                                }
                            }
                            break;
                        default:
                            throw new InvalidDataException($"Invalid installer package item type \"{type}\"");
                    }
                }
                config.UncompressedSource = null;
            }
        }
    }
}

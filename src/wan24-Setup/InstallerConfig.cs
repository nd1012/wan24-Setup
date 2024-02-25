using wan24.Compression;
using wan24.Core;

namespace wan24.Setup
{
    /// <summary>
    /// Installer configuration
    /// </summary>
    public record class InstallerConfig
    {
        /// <summary>
        /// Constructor
        /// </summary>
        public InstallerConfig() { }

        /// <summary>
        /// Installer package source stream
        /// </summary>
        public required Stream Source { get; init; }

        /// <summary>
        /// App path
        /// </summary>
        public string? AppPath { get; set; }

        /// <summary>
        /// Custom setup CLI arguments
        /// </summary>
        public string[]? Arguments { get; init; }

        /// <summary>
        /// Skip exit for running the setup
        /// </summary>
        public bool SkipExit { get; init; }

        /// <summary>
        /// Skip running the setup?
        /// </summary>
        public bool SkipSetup { get; init; }

        /// <summary>
        /// Compression options
        /// </summary>
        public CompressionOptions Compression { get; set; } = BrotliCompressionAlgorithm.Instance.DefaultOptions with
        {
            SerializerVersionIncluded = true,
            Algorithm = BrotliCompressionAlgorithm.ALGORITHM_NAME,
            AlgorithmIncluded = false,
            FlagsIncluded = true,
            UncompressedLengthIncluded = true,
            LeaveOpen = true
        };

        /// <summary>
        /// Cancellation token
        /// </summary>
        public CancellationToken Cancellation { get; init; }

        /// <summary>
        /// Temporary folder whch contains the extracted installer package files and folders
        /// </summary>
        public string TempPath { get; set; } = Path.GetFullPath($"{Path.Combine(Settings.TempFolder, Guid.NewGuid().ToString())}/");

        /// <summary>
        /// Delete the temporary folder after setup? (won't be deleted, if the installer has to exit for running the setup!)
        /// </summary>
        public bool DeleteTempPath { get; init; } = true;

        /// <summary>
        /// Setup runtime configuration (available at runtime when the setup is going to run)
        /// </summary>
        public SetupConfig? SetupConfig { get; internal set; }

        /// <summary>
        /// Uncompressed source stream (available at runtime during extracting the installer package contents)
        /// </summary>
        public Stream? UncompressedSource { get; internal set; }

        /// <summary>
        /// Any tagged data
        /// </summary>
        public Dictionary<string, object> Data { get; } = [];

        /// <summary>
        /// Progress
        /// </summary>
        public ProcessingProgress? Progress { get; init; }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using wan24.Compression;
using wan24.Core;
using wan24.StreamSerializerExtensions;

namespace wan24.Setup
{
    /// <summary>
    /// Setup installer
    /// </summary>
    public static partial class Installer
    {
        /// <summary>
        /// CLI argument name for the process ID to wait for before running the setup
        /// </summary>
        public const string ARG_PID = "pid";
        /// <summary>
        /// CLI argument name for the app path
        /// </summary>
        public const string ARG_PATH = "path";
        /// <summary>
        /// CLI argument name for the command to execute after the setup was done
        /// </summary>
        public const string ARG_CMD = "cmd";
        /// <summary>
        /// CLI argument name for the command arguments to use for the command to execute after the setup was done
        /// </summary>
        public const string ARG_ARGS = "args";
        /// <summary>
        /// Setup configuration filename
        /// </summary>
        public const string SETUP_CONFIG_FILENAME = "setup.json";

        /// <summary>
        /// Currently running setup arguments (available during setup)
        /// </summary>
        public static CliArguments? Arguments { get; private set; }

        /// <summary>
        /// App path (available during setup)
        /// </summary>
        public static string? AppPath { get; private set; }

        /// <summary>
        /// Command to execute after the setup was done (available during setup)
        /// </summary>
        public static string? Command { get; private set; }

        /// <summary>
        /// Command arguments to use for the command to execute after the setup was done (available during setup)
        /// </summary>
        public static string? CommandArguments { get; private set; }
    }
}

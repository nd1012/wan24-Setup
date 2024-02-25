using System.Diagnostics;
using System.Reflection;
using wan24.Core;

namespace wan24.Setup
{
    // Setup
    public static partial class Installer
    {
        /// <summary>
        /// Run a setup
        /// </summary>
        /// <param name="config">Installer configuration</param>
        /// <returns>Installer exit code, STDOUT and STDERR contents (only if no exit was required), and if an exit of the running app is required</returns>
        public static async Task<(int ExitCode, string StdOut, string StdErr, bool RequireExit)> RunSetupAsync(InstallerConfig config)
        {
            // Create a temporary folder
            if (!Directory.Exists(config.TempPath))
                if (ENV.IsLinux)
                {
#pragma warning disable CA1416 // Not available on all platforms
                    Directory.CreateDirectory(config.TempPath, Settings.CreateFolderMode);
#pragma warning restore CA1416 // Not available on all platforms
                }
                else
                {
                    Directory.CreateDirectory(config.TempPath);
                }
            bool error = false;
            try
            {
                // Unpack the installer package to the temporary folder
                await ExtractInstallerPackageAsync(config).DynamicContext();
                // Run the setup
                int exitCode;
                string stdOut, stdErr;
                bool requireExit = !config.SkipExit;
                if (!config.SkipSetup)
                {
                    string configFile = Path.Combine(config.TempPath, SETUP_CONFIG_FILENAME);
                    if (!File.Exists(configFile)) throw new FileNotFoundException("Missing setup configuration file", configFile);
                    config.SetupConfig = JsonHelper.Decode<SetupConfig>(await File.ReadAllTextAsync(configFile, config.Cancellation).DynamicContext())
                        ?? throw new InvalidDataException("Failed to load the setup configuration");
                    using (Process proc = new())
                    {
                        proc.StartInfo.RedirectStandardError = true;
                        proc.StartInfo.RedirectStandardOutput = true;
                        proc.StartInfo.WorkingDirectory = config.TempPath;
                        proc.StartInfo.FileName = config.SetupConfig.Command;
                        if (config.SetupConfig.Arguments is not null) proc.StartInfo.Arguments = config.SetupConfig.Arguments;
                        proc.StartInfo.ArgumentList.AddRange($"--{ARG_PID}", config.SkipExit ? "-1" : Environment.ProcessId.ToString());
                        proc.StartInfo.ArgumentList.AddRange($"--{ARG_PATH}", config.AppPath ?? Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!);
                        if (config.Arguments is not null) proc.StartInfo.ArgumentList.AddRange(config.Arguments);
                        proc.StartInfo.UseShellExecute = true;
                        if (config.SetupConfig.HideWindow || !config.SetupConfig.ExitRequired)
                        {
                            proc.StartInfo.CreateNoWindow = true;
                            proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                        }
                        if (config.SetupConfig.RequireAdministratorPrivileges) proc.StartInfo.Verb = "runas";
                        Logging.WriteInfo($"Running setup from \"{config.TempPath}\"");
                        proc.Start();
                        if (config.SetupConfig.ExitRequired && !config.SkipExit) return (0, string.Empty, string.Empty, true);
                        await proc.WaitForExitAsync(config.Cancellation).DynamicContext();
                        exitCode = proc.ExitCode;
                        Logging.WriteInfo($"Setup exit code was #{exitCode}");
                        stdOut = await proc.StandardOutput.ReadToEndAsync(config.Cancellation).DynamicContext();
                        stdErr = await proc.StandardError.ReadToEndAsync(config.Cancellation).DynamicContext();
                        error = exitCode != 0;
                    }
                }
                else
                {
                    exitCode = 0;
                    stdOut = string.Empty;
                    stdErr = string.Empty;
                }
                // Clean up
                if (!error && config.DeleteTempPath) Directory.Delete(config.TempPath, recursive: true);
                Logging.WriteInfo("Setup done");
                return (exitCode, stdOut, stdErr, requireExit);
            }
            catch
            {
                if (error)
                {
                    Logging.WriteError($"Setup failed with exception, leaving temporary folder \"{config.TempPath}\"");
                }
                else if(config.DeleteTempPath)
                {
                    try
                    {
                        Directory.Delete(config.TempPath, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        Logging.WriteError($"Failed to delete temporary folder \"{config.TempPath}\": {ex}");
                    }
                }
                throw;
            }
            finally
            {
                config.UncompressedSource = null;
                config.SetupConfig = null;
            }
        }

        /// <summary>
        /// Run the setup
        /// </summary>
        /// <param name="args">CLI arguments</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Exit code</returns>
        public static async Task<int> RunSetupAsync(CliArguments args, CancellationToken cancellationToken = default)
        {
            if (Arguments is not null) throw new InvalidOperationException("Setup running already");
            try
            {
                Arguments = args;
                string configFile = Path.Combine(Path.GetFullPath("./"), SETUP_CONFIG_FILENAME);
                if (!File.Exists(configFile)) throw new FileNotFoundException("Missing setup configuration file", configFile);
                SetupConfig config = JsonHelper.Decode<SetupConfig>(await File.ReadAllTextAsync(configFile, cancellationToken).DynamicContext())
                    ?? throw new InvalidDataException("Failed to load the setup configuration");
                if (args.HasValues(ARG_PID))
                {
                    int pid = args.SingleJson<int>(ARG_PID);
                    using Process? proc = pid < 0 ? null : Process.GetProcesses().FirstOrDefault(p => p.Id == pid);
                    if (proc is not null)
                    {
                        Logging.WriteInfo($"Waiting for setup calling process #{pid} to exit");
                        await proc.WaitForExitAsync(cancellationToken).DynamicContext();
                        Logging.WriteInfo("Setup calling process did exit");
                    }
                    else if (pid >= 0)
                    {
                        Logging.WriteInfo($"Setup calling process #{pid} did exit already");
                    }
                }
                AppPath = args.Single(ARG_PATH);
                Command = args.HasValues(ARG_CMD) ? args.All(ARG_CMD).First() : null;
                CommandArguments = args.HasValues(ARG_ARGS) ? args.All(ARG_CMD).First() : null;
                TypeHelper.Instance.ScanAssemblies();
                Type installerType = (from ass in TypeHelper.Instance.Assemblies
                                      from type in ass.GetTypes()
                                      where typeof(ISetup).IsAssignableFrom(type) &&
                                        type.CanConstruct()
                                      select type)
                                      .FirstOrDefault() ?? throw new InvalidProgramException("Installer not found");
                Logging.WriteInfo($"Running installer \"{installerType}\"");
                int res;
                ISetup installer = installerType.ConstructAuto() as ISetup ?? throw new InvalidProgramException($"Failed to instance {installerType}");
                try
                {
                    res = await installer.RunAsync(cancellationToken).DynamicContext();
                    Logging.WriteInfo($"Installer done with exit code #{res}");
                }
                finally
                {
                    await installer.TryDisposeAsync().DynamicContext();
                }
                if (Command is null || !config.ExitRequired) return res;
                Logging.WriteInfo("Executing command after setup");
                using (Process proc = new())
                {
                    proc.StartInfo.WorkingDirectory = AppPath;
                    proc.StartInfo.FileName = Command;
                    if (CommandArguments is not null) proc.StartInfo.Arguments = CommandArguments;
                    proc.StartInfo.ArgumentList.AddRange($"--{ARG_PID}", Environment.ProcessId.ToString());
                    proc.StartInfo.ArgumentList.AddRange($"--{ARG_PATH}", Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!);
                    proc.StartInfo.UseShellExecute = true;
                    proc.StartInfo.CreateNoWindow = true;
                    proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                    proc.Start();
                }
                return res;
            }
            finally
            {
                Arguments = null;
                AppPath = null;
                Command = null;
                CommandArguments = null;
            }
        }
    }
}

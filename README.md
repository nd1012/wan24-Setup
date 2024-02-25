# wan24-Setup

This library contains a setup helper which you can use to run updates from 
within your app. It has

- an installer package creation method
- an installer run method (to run an installer package from the running app)
- a setup run method (for running the setup in a separate process)
- an interface for your custom setup class (which will be used from the setup 
run method)
- a setup configuration class
- a helper method to copy all files and folders from a folder to another 
folder, excluding the setup and configuration
- a .NET tool

The library supports

- creating compressed installer packages
- extracting an installer package
- running an included setup, optional without quitting the running app
- running the setup with administrator privileges

The .NET tool supports:

- creating compressed installer packages
- extracting an installer package
- running an installer

## How to get it

This library is available as 
[NuGet package](https://www.nuget.org/packages/wan24-Setup/).

These extension NuGet packages are available:

- [wan24-Setup-Tool (adopts.NET tool)](https://www.nuget.org/packages/wan24-Setup-Tool/)

## Usage

The library needs to be referenced from your app and the setup, which you 
include in the installer package.

### Contents of an installer package and configuration

Create your required installer files and folder structure in a separate 
folder. In the root folder there must be

- the setup executable
- the `setup.json` configuration, which can construct a `SetupConfig` object

Everything else is up to you.

If the `SetupConfig.ExitRequired` was set to `true`, the app will exit during 
running the setup. The `SetupConfig.Command` contains the command to execute 
for running the setup. If `SetupConfig.RequireAdministratorPrivileges` was set 
to `true`, the setup command will be executed with administrator privileges.

### Using the `ISetup` interface for crating your setup logic

In your setup executable include a class which implements the `ISetup` 
interface:

```cs
public sealed class YourSetup : ISetup
{
	public async Task<int> RunAsync(CancellationToken cancellationToken)
	{
		// Perform your setup here
		return 0;
	}
}
```

Your setup method may call `Installer.CopyFilesAsync` to copy all files and 
folders from the temporary installer folder to the app folder, except the 
setup and the configuration. You may specify more file- and foldernames to 
exclude.

**TIP**: If you place the files/folders to copy in a sub-folder and set the 
working directory to that folder, `CopyFilesAsync` will use only that sub-
folder.

**NOTE**: If the setup class can be disposed, it will be disposed.

Your setup can access environment information from the static `Installer` 
properties. When the method is being called, the setup calling app did exit 
already, if requested.

Your setup executable startup could look like this:

```cs
Logging.Logger = new ConsoleLogger(
	LogLevel.Information, 
	await FileLogger.CreateAsync("setup.log")
	);
return await Installer.RunSetupAsync(args);
```

### Creating an installer package

Call the installer package creation method:

```cs
long uncompressedLengthInBytes = await Installer.CreateInstallerPackageAsync(
	"/path/to/installer.pkg",
	"/path/to/installer/structure/",
	Directory.GetFileSystemEntries("/path/to/installer/structure", "*", SearchOption.AllDirectories)
	);
```

You can also use the CLI tool from the "wan24-Setup CLI" project:

```bash
installerPackage --create "/path/to/installer.pkg" --path "/path/to/installer/structure/"
```

**NOTE**: The base path must end with a slash!

### Installing a package

Example:

```cs
(int setupExitCode, string stdOut, string stdErr, bool requireExit) = 
	await Installer.RunSetupAsync(installerPackageStream);
if(requireExit) // Exit your app to run the setup
```

**NOTE**: If the setup requires the app to exit, the Window of the setup 
process won't be hidden unless the `HideWindow` configuration option was set 
to `true`.

If the installer failed, the used temporary folder won't be deleted after 
running the setup. See the logs for the used temporary folder (`wan24-Setup` 
uses `wan24.Core.Logging`, which should be configured properly before).

**CAUTION**: Be sure to ensure the trustability of the installer package 
BEFORE calling the `RunSetupAsync` method! You could do that using a 
signature, such as the .NET tool provides one.

You may also use additional arguments for the setup to execute a command after 
the setup was done:

- `--cmd [COMMAND]`: Command to execute
- `--args [ARGUMENTS]`: Arguments for the command to execute

**NOTE**: The command will not be executed, if the setup failed!

## Using the .NET tool `wan24-Setup-Tool`

Install:

```bash
dotnet tool install --global wan24setup
```

Usage (the command will display a help):

```bash
dotnet tool run wan24setup -help
```

Errors will be written to STDERR, output to STDOUT. The exit code is zero, if 
succeed.

## Good to know

The installer package will be compressed using Brotli. You can call 
`RunSetupAsync` with a network stream to extract the installer package during 
downloading without having to store it.

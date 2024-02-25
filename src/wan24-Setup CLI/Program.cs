using Microsoft.Extensions.Logging;
using System.Net.Mail;
using System.Reflection;
using System.Security.Cryptography;
using wan24.Core;
using wan24.Crypto;
using wan24.Crypto.BC;
using wan24.Crypto.NaCl;
using wan24.Crypto.TPM;
using wan24.Setup;
using wan24.StreamSerializerExtensions;

//TODO Reference NuGet package wan24-Setup

CliArguments arguments = new(args);

static int DisplayHelp(in int exitCode = 1)
{
    Console.Error.WriteLine(wan24.Setup.Properties.Resources.Help);
    return exitCode;
}

static void SetupCrypto()
{
    wan24.Crypto.Bootstrap.Boot();
    wan24.Crypto.BC.Bootstrap.Boot();
    wan24.Crypto.NaCl.Bootstrap.Boot();
    wan24.Crypto.TPM.Bootstrap.Boot();
    if (!Shake128.IsSupported) BouncyCastle.ReplaceNetAlgorithms();
    //TODO Setup PKI
}

SecureByteArray GetPwd()
{
    using SecureByteArrayStructSimple pwd = new(arguments.Single("pwd").GetBytes());
    using SecureByteArrayStructSimple pwdHash = new(HashHelper.GetAlgorithm(HashSha3_512Algorithm.ALGORITHM_NAME).Hash(pwd.Array));
    (byte[] stretchedPwd, _) = KdfPbKdf2Algorithm.Instance.Stretch(pwd.Array, HashSha3_512Algorithm.HASH_LENGTH, pwdHash.Array);
    using SecureByteArrayStructSimple pwdStretched = new(stretchedPwd);
    (stretchedPwd, _) = KdfArgon2IdAlgorithm.Instance.Stretch(pwdStretched.Array, HashSha3_512Algorithm.HASH_LENGTH, pwdHash.Array);
    using SecureByteArrayStructSimple pwdStretched2 = new(stretchedPwd);
    return new(arguments["tpm"] ? Tpm2Helper.Hmac(pwdStretched2.Array) : pwdStretched2.Array.CloneArray());
}

#if DEBUG
Logging.Logger = new ConsoleLogger(LogLevel.Debug);
#else
Logging.Logger = new ConsoleLogger(LogLevel.Information);
#endif
await wan24.Core.Bootstrap.Async().DynamicContext();
//TODO Implement signature and key generation
if (arguments["v"] || arguments["version"])
{
    Console.WriteLine(Environment.Version.ToString());
    return 0;
}
if (!arguments["path", true] || arguments["h"] || arguments["help"])
{
    Console.Error.WriteLine(wan24.Setup.Properties.Resources.Help);
    return DisplayHelp(arguments["h"] || arguments["help"] ? 0 : 1);
}
string path = Path.GetFullPath(arguments.Single("path"));
if (!path.EndsWith('/')) path = $"{path}/";
if (arguments["createkey"])
{
    if (
        !arguments["email", true] ||
        !MailAddress.TryCreate(arguments.Single("email"), out _) ||
        !arguments["pwd", true] ||
        Environment.GetEnvironmentVariable(arguments.Single("pwd")) is null
        )
        return DisplayHelp();
    if (arguments["tpm"] && !await Tpm2Helper.IsAvailableAsync().DynamicContext())
    {
        Console.Error.WriteLine("TPM not available");
        return 1;
    }
    SetupCrypto();
    FileStreamOptions fso = new()
    {
        Mode = FileMode.OpenOrCreate,
        Access = FileAccess.ReadWrite,
        Share = FileShare.None
    };
#pragma warning disable CA1416 // Not available on all platforms
    if (ENV.IsLinux) fso.UnixCreateMode = Settings.CreateFileMode;
#pragma warning restore CA1416 // Not available on all platforms
    AsymmetricPublicKeySigningRequest ksr;
    using (PrivateKeySuite keys = new())
    {
        keys.SignatureKey = await AsymmetricEcDsaAlgorithm.Instance.CreateKeyPairAsync();
        keys.CounterSignatureKey = await AsymmetricDilithiumAlgorithm.Instance.CreateKeyPairAsync();
        using SecureByteArray pwd = GetPwd();
        FileStream keysFile = new(Path.Combine(path, "private.key"), fso);
        await using (keysFile.DynamicContext())
        {
            if (keysFile.Length != 0) keysFile.SetLength(0);
            await keysFile.WriteAsync(keys.Encrypt(pwd.Array, new()
            {
                Algorithm = EncryptionSerpent256CbcAlgorithm.ALGORITHM_NAME
            })).DynamicContext();
            Logging.WriteInfo($"Private key written to {Path.Combine(path, "private.key")}");
        }
        ksr = new(keys.SignatureKey.PublicKey, new()
        {
            {SignedAttributes.OWNER_IDENTIFIER, arguments.Single("email").ToLower() },
            {SignedAttributes.PKI_DOMAIN, "wan24setup" }
        }, "wan24setup");
        ksr.SignRequest(keys.SignatureKey, new()
        {
            PrivateKey = keys.SignatureKey,
            CounterPrivateKey = keys.CounterSignatureKey
        });
    }
    try
    {
        FileStream ksrFile = new(Path.Combine(path, "public.ksr"), fso);
        await using (ksrFile.DynamicContext())
        {
            if (ksrFile.Length != 0) ksrFile.SetLength(0);
            await ksrFile.WriteAsync(ksr.ToBytes());
            Logging.WriteInfo($"Public key signing request written to {Path.Combine(path, "public.ksr")}");
        }
    }
    finally
    {
        ksr.Dispose();
    }
}
else if (arguments["create", true])
{
    if (arguments["sign", true])
    {
        if (
            !arguments["signed", true] ||
            !arguments["pwd"] ||
            Environment.GetEnvironmentVariable(arguments.Single("pwd")) is null
            )
            return DisplayHelp();
        if (arguments["tpm"] && !await Tpm2Helper.IsAvailableAsync().DynamicContext())
        {
            Console.Error.WriteLine("TPM not available");
            return 1;
        }
        SetupCrypto();
    }
    Console.WriteLine(await Installer.CreateInstallerPackageAsync(arguments.Single("create"), path, Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories)).DynamicContext());
    if (arguments["sign"])
    {
        SignatureContainer signature;
        using (AsymmetricSignedPublicKey publicKey = (AsymmetricSignedPublicKey)await File.ReadAllBytesAsync(arguments.Single("signed")).DynamicContext())
        {
            await publicKey.ValidateAsync(options: new()
            {
                AllowedValidationDomains = new string[] { "wan24setup" }.AsReadOnly()
            });//TODO Add online validation
            using MemoryPoolStream privateKeysData = new()
            {
                CleanReturned = true
            };
            using (SecureByteArray pwd = GetPwd())
            {
                using PrivateKeySuite privateKeys = PrivateKeySuite.Decrypt(await File.ReadAllBytesAsync(arguments.Single("sign")).DynamicContext(), pwd.Array, new()
                {
                    Algorithm = EncryptionSerpent256CbcAlgorithm.ALGORITHM_NAME
                });
                pwd.Dispose();
                if (privateKeys.SignatureKey is null)
                {
                    Console.Error.WriteLine("Missing private signature key");
                    return 1;
                }
                if (privateKeys.CounterSignatureKey is null)
                {
                    Console.Error.WriteLine("Missing private counter signature key");
                    return 1;
                }
                if (!privateKeys.SignatureKey.ID.SequenceEqual(publicKey.PublicKey.ID))
                {
                    Console.Error.WriteLine("Signature key ID mismatch");
                    return 1;
                }
                FileStream pkg = new(arguments.Single("create"), FileMode.Open, FileAccess.Read, FileShare.Read);
                await using (pkg.DynamicContext()) signature = await privateKeys.SignatureKey.SignDataAsync(pkg, "wan24setup", new()
                {
                    PrivateKey = privateKeys.SignatureKey,
                    CounterPrivateKey = privateKeys.CounterSignatureKey
                }).DynamicContext();
            }
        }
        FileStreamOptions fso = new()
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None
        };
#pragma warning disable CA1416 // Not available on all platforms
        if (ENV.IsLinux) fso.UnixCreateMode = Settings.CreateFileMode;
#pragma warning restore CA1416 // Not available on all platforms
        FileStream sig = new($"{arguments.Single("create")}.sig", fso);
        await using (sig.DynamicContext())
        {
            if (sig.Length != 0) sig.SetLength(0);
            await sig.WriteAsync(signature.ToBytes()).DynamicContext();
        }
        Logging.WriteInfo($"Signature written to \"{arguments.Single("create")}.sig\"");
    }
}
else if (arguments["extract", true])
{
    if (!Directory.Exists(path))
        if (ENV.IsLinux)
        {
#pragma warning disable CA1416 // Not available on all platforms
            Directory.CreateDirectory(path, Settings.CreateFolderMode);
#pragma warning restore CA1416 // Not available on all platforms
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    FileStream source = new(arguments.Single("extract"), FileMode.Open, FileAccess.Read, FileShare.Read);
    await using (source.DynamicContext()) await Installer.ExtractInstallerPackageAsync(source, path).DynamicContext();
}
else if (arguments["install", true])
{
    if (!Directory.Exists(path))
        if (ENV.IsLinux)
        {
#pragma warning disable CA1416 // Not available on all platforms
            Directory.CreateDirectory(path, Settings.CreateFolderMode);
#pragma warning restore CA1416 // Not available on all platforms
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    Stream source;
    HttpClient? httpClient = null;
    HttpResponseMessage? response = null;
    try
    {
        int exitCode;
        string stdOut, stdErr;
        if (Uri.TryCreate(arguments.Single("install"), UriKind.Absolute, out Uri? uri) && uri.Scheme.ToLower().In(["http", "https"]))
        {
            httpClient = new();
            using (HttpRequestMessage request = new(HttpMethod.Get, uri)) response = await httpClient.SendAsync(request).DynamicContext();
            response.EnsureSuccessStatusCode();
            source = response.Content.ReadAsStream();
        }
        else
        {
            source = new FileStream(arguments.Single("install"), FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        await using (source.DynamicContext()) (exitCode, stdOut, stdErr, _) = await Installer.RunSetupAsync(new()
        {
            Source = source,
            TempPath = path,
            Arguments = args,
            SkipExit = true
        }).DynamicContext();
        if (stdOut.Length != 0) Console.Write(stdOut);
        if (stdErr.Length != 0) Console.Error.Write(stdErr);
        return exitCode;
    }
    finally
    {
        response?.Dispose();
        httpClient?.Dispose();
    }
}
else
{
    return DisplayHelp();
}
return 0;

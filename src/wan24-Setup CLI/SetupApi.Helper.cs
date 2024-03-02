using System.Text;
using Tpm2Lib;
using wan24.CLI;
using wan24.Core;
using wan24.Crypto;
using wan24.Crypto.BC;
using wan24.Crypto.NaCl;
using wan24.Crypto.TPM;
using wan24.StreamSerializerExtensions;

namespace wan24.Setup.CLI
{
    // Helper
    public sealed partial class SetupApi
    {
        /// <summary>
        /// Name of the argument for the global CSRNG stream cipher algorithm
        /// </summary>
        public const string GLOBAL_CSRNG = "globalCsRng";
        /// <summary>
        /// Name of the flag to disable global RNG online seeding
        /// </summary>
        public const string NO_GLOBAL_ONLINE_SEED_FLAG = "noGlobalOnlineSeed";
        /// <summary>
        /// Name of the argument for the global RNG online seed URI
        /// </summary>
        public const string GLOBAL_ONLINE_SEED_URI = "globalOnlineSeedUri";
        /// <summary>
        /// Name of the argument for the global RNG online seed length
        /// </summary>
        public const string GLOBAL_ONLINE_SEED_LENGTH = "globalOnlineSeedLength";
        /// <summary>
        /// Name of the argument for the global vendor PKI filename
        /// </summary>
        public const string GLOBAL_VENDOR_PKI = "globalVendorPki";

        /// <summary>
        /// Is the crypto environment initialized?
        /// </summary>
        private static bool CryptoInitialized = false;

        /// <summary>
        /// Singleton TPM engine
        /// </summary>
        public static Tpm2Engine? TpmEngine { get; private set; }

        /// <summary>
        /// Max. TPM digest
        /// </summary>
        public static TpmAlgId TpmMaxDigest { get; private set; } = TpmAlgId.Any;

        /// <summary>
        /// Initialize the crypto environment
        /// </summary>
        /// <param name="output">Output something to the console?</param>
        /// <param name="rng">Initialize the RNG?</param>
        /// <param name="tpm">Initialize the TPM?</param>
        /// <param name="pki">Initialize the PKI?</param>
        public static async Task InitCryptoAsync(bool output = false, bool rng = true, bool tpm = true, bool pki = true)
        {
            if (CryptoInitialized) return;
            CryptoInitialized = true;
            if (output)
            {
                Console.WriteLine("Initializing crypto environment...");
                Console.WriteLine($"RNG: {rng}");
                Console.WriteLine($"TPM: {tpm}");
                Console.WriteLine($"PKI: {pki}");
            }
            if (CliApi.CurrentContext?.Arguments is null) throw new InvalidOperationException("Must be called during executing the CLI API");
            // Bootstrapper
            Crypto.Bootstrap.Boot();
            Crypto.BC.Bootstrap.Boot();
            Crypto.NaCl.Bootstrap.Boot();
            Crypto.TPM.Bootstrap.Boot();
            BouncyCastle.ReplaceNetAlgorithms();
            BouncyCastle.SetDefaults();
            NaClHelper.SetDefaults();
            // Initialize wan24-TPM
            if (tpm)
            {
                if (await Tpm2Helper.IsAvailableAsync().DynamicContext())
                {
                    if (output) Console.WriteLine("Initializing TPM crypto environment...");
                    TpmEngine = await Tpm2Engine.CreateAsync().DynamicContext();
                    Tpm2Helper.DefaultEngine = TpmEngine;
                    TpmMaxDigest = Tpm2Helper.GetDigestAlgorithm(Tpm2Helper.GetMaxDigestSize(TpmEngine));
                    if (output) Console.WriteLine($"TPM max. digest: {TpmMaxDigest}");
                    if (TpmMaxDigest < TpmAlgId.Sha512)
                    {
                        MacHelper.Algorithms.TryRemove(MacTpmHmacSha512Algorithm.ALGORITHM_NAME, out _);
                        if (TpmMaxDigest < TpmAlgId.Sha384) MacHelper.Algorithms.TryRemove(MacTpmHmacSha384Algorithm.ALGORITHM_NAME, out _);
                        // SHA-256 must be supported
                    }
                }
                else
                {
                    if (output) Console.WriteLine("TPM not available");
                    TpmMaxDigest = TpmAlgId.None;
                }
            }
            else
            {
                if (output) Console.WriteLine("Skipping TPM initialization");
                TpmMaxDigest = TpmAlgId.None;
            }
            // Initialize the RNG
            if (rng)
            {
                if (output)
                {
                    Console.WriteLine("Initializing random number generator...");
                    Console.WriteLine($"CSRNG cipher: {(CliApi.CurrentContext.Arguments[GLOBAL_CSRNG, requireValues: true] ? EncryptionHelper.GetAlgorithm(CliApi.CurrentContext.Arguments.Single(GLOBAL_CSRNG)).DisplayName : EncryptionChaCha20Algorithm.Instance.DisplayName)}");
                }
                RND.UseDevRandom = RND.HasDevRandom;
                List<IRng> rngs = [];
                if (RND.HasDevRandom) rngs.Add(new DevRandomRng());
                if (TpmEngine is not null) rngs.Add(new TpmRng(TpmEngine));
                StreamCipherRng csRng = CliApi.CurrentContext.Arguments[GLOBAL_CSRNG, requireValues: true]
                    ? new StreamCipherRng(EncryptionHelper.GetAlgorithm(CliApi.CurrentContext.Arguments.Single(GLOBAL_CSRNG)))
                    : new ChaCha20Rng();
                rngs.Add(csRng);
                RND.Generator = rngs.Count == 1 ? csRng : new DisposableXorRng([.. rngs]);
                RND.SeedConsumer = csRng;
                if (!CliApi.CurrentContext.Arguments[NO_GLOBAL_ONLINE_SEED_FLAG])
                {
                    if (output) Console.WriteLine("Seeding RNG...");
                    Console.WriteLine($"Online seed: {(CliApi.CurrentContext.Arguments[GLOBAL_ONLINE_SEED_URI, requireValues: true] ? CliApi.CurrentContext.Arguments.Single(GLOBAL_ONLINE_SEED_URI) : RngOnlineSeedTimer.DEFAULT_URI)} ({(CliApi.CurrentContext.Arguments[GLOBAL_ONLINE_SEED_LENGTH, requireValues: true] ? CliApi.CurrentContext.Arguments.Single(GLOBAL_ONLINE_SEED_LENGTH) : RngOnlineSeedTimer.DEFAULT_SEED_LENGTH)} bytes)");
                    RngOnlineSeedTimer onlineSeed = new(
                        uri: CliApi.CurrentContext.Arguments[GLOBAL_ONLINE_SEED_URI, requireValues: true]
                            ? CliApi.CurrentContext.Arguments.Single(GLOBAL_ONLINE_SEED_URI)
                            : RngOnlineSeedTimer.DEFAULT_URI,
                        length: CliApi.CurrentContext.Arguments[GLOBAL_ONLINE_SEED_LENGTH, requireValues: true]
                            ? int.Parse(CliApi.CurrentContext.Arguments.Single(GLOBAL_ONLINE_SEED_LENGTH))
                            : RngOnlineSeedTimer.DEFAULT_SEED_LENGTH
                        );
                    await using (onlineSeed.DynamicContext()) await onlineSeed.SeedAsync().DynamicContext();
                }
                else if (output)
                {
                    Console.WriteLine("Skipping RNG online seeding");
                }
            }
            else if (output)
            {
                Console.WriteLine("Skipping RNG initialization");
            }
            // Initialize the PKI
            if (pki)
            {
                if (output) Console.WriteLine("Initializing PKI...");
                if (CliApi.CurrentContext.Arguments[GLOBAL_VENDOR_PKI, requireValues: true])
                {
                    if (output) Console.WriteLine("Using custom PKI");
                    FileStream pkiFile = FsHelper.CreateFileStream(CliApi.CurrentContext.Arguments.Single(GLOBAL_VENDOR_PKI), FileMode.Open, FileAccess.Read, FileShare.Read);
                    await using (pkiFile.DynamicContext())
                        CryptoEnvironment.PKI = await pkiFile.ReadSerializedAsync<SignedPkiStore>(await pkiFile.ReadSerializerVersionAsync().DynamicContext()).DynamicContext();
                }
                else
                {
                    if (output) Console.WriteLine("Using vendor PKI");
                    //TODO Use vendor PKI
                }
                CryptoEnvironment.PKI?.EnableLocalPki();
            }
            else if (output)
            {
                Console.WriteLine("Skipping PKI initialization");
            }
        }

        /// <summary>
        /// Read the password
        /// </summary>
        /// <param name="pwdVar">Environment variable name which contains the encryption password to use (if <see langword="null"/>, the password is required to be given from STDIN)</param>
        /// <param name="output">Write something to the console?</param>
        /// <returns>Password</returns>
        public static async Task<byte[]> ReadPasswordAsync(string? pwdVar, bool output)
        {
            if (pwdVar is not null)
            {
                // From an environment variable
                if (output) Console.WriteLine("Use an environment variable");
                return Environment.GetEnvironmentVariable(pwdVar)?.GetBytes() ?? throw new ArgumentException("Environment variable not found", nameof(pwdVar));
            }
            // From STDIN
            if (output) Console.WriteLine("Read the password from STDIN");
            using Stream stdIn = Console.OpenStandardInput();
            using MemoryPoolStream ms = new()
            {
                CleanReturned = true
            };
            using LimitedLengthStream msLimit = new(ms, byte.MaxValue);
            await stdIn.CopyToAsync(msLimit).DynamicContext();
            return ms;
        }

        /// <summary>
        /// Finalize the password
        /// </summary>
        /// <param name="pwd">Password</param>
        /// <param name="tpm">Use a TPM?</param>
        /// <param name="output">Write something to the console?</param>
        /// <returns>Finalized password</returns>
        public static async Task<SecureByteArray> FinalizePasswordAsync(byte[] pwd, bool tpm, bool output)
        {
            await InitCryptoAsync(output, rng: false, tpm, pki: false).DynamicContext();
            if (tpm && TpmEngine is null) throw new ArgumentException("TPM not available", nameof(tpm));
            //TODO Use DefaultPasswordPostProcessor
            // Apply KDF
            using SecureByteArrayStructSimple salt = new(pwd.Mac(pwd, new()
            {
                MacAlgorithm = MacHmacSha3_512Algorithm.ALGORITHM_NAME
            }));
            if (output) Console.WriteLine($"Key stretching: {KdfHelper.GetAlgorithm(KdfPbKdf2Algorithm.ALGORITHM_VALUE).DisplayName} with {HashHelper.GetAlgorithm(HashSha3_384Algorithm.ALGORITHM_NAME).DisplayName} hash and 250.000 iterations, then {KdfHelper.GetAlgorithm(KdfArgon2IdAlgorithm.ALGORITHM_VALUE).DisplayName} with 46M memory limit");
            CryptoOptions kdfOptions = new()
            {
                KdfAlgorithm = KdfPbKdf2Algorithm.ALGORITHM_NAME,
                KdfOptions = new KdfPbKdf2Options()
                {
                    HashAlgorithm = HashSha3_384Algorithm.ALGORITHM_NAME
                },
                KdfIterations = 250000,
                CounterKdfAlgorithm = KdfArgon2IdAlgorithm.ALGORITHM_NAME,
                CounterKdfOptions = new KdfArgon2IdOptions()
                {
                    Parallelism = 1,
                    MemoryLimit = 47104
                },
                CounterKdfIterations = 250000// Dummy value (Argon2 doesn't use iterations)
            };
            (kdfOptions.Password, kdfOptions.CounterKdfSalt) = pwd.Stretch(salt.Length, salt.Array, kdfOptions);
            try
            {
                HybridAlgorithmHelper.StretchPassword(kdfOptions);
            }
            catch
            {
                kdfOptions.Clear();
                throw;
            }
            if (!tpm) return new(kdfOptions.Password);
            // Apply TPM HMACs
            using SecureByteArrayStructSimple stretchedPwd = new(kdfOptions.Password);
            if (output) Console.WriteLine($"TPM2 HMAC ({TpmMaxDigest})");
            return new(Tpm2Helper.Hmac(stretchedPwd.Array, key: stretchedPwd.Array, engine: TpmEngine!));
        }
    }
}

using System.Security.Cryptography;
using System.Text;
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
        /// Is the crypto environment initialized?
        /// </summary>
        private static bool CryptoInitialized = false;

        /// <summary>
        /// Initialize the crypto environment
        /// </summary>
        public static async Task InitCryptoAsync()
        {
            if (CryptoInitialized) return;
            CryptoInitialized = true;
            if (CliApi.CurrentContext?.Arguments is null) throw new InvalidOperationException();
            // Bootstrapper
            Crypto.Bootstrap.Boot();
            Crypto.BC.Bootstrap.Boot();
            Crypto.NaCl.Bootstrap.Boot();
            Crypto.TPM.Bootstrap.Boot();
            // Replace unsupported .NET algorithms
            if (!Shake128.IsSupported) BouncyCastle.ReplaceNetAlgorithms();
            // Initialize wan24-TPM
            if (await Tpm2Helper.IsAvailableAsync().DynamicContext())
                Tpm2Helper.DefaultEngine = Tpm2Helper.CreateEngine();
            // Initialize the RNG
            RND.UseDevRandom = RND.RequireDevRandom = ENV.IsLinux;
            ChaCha20Rng rng = new();
            RND.Generator = rng;
            RND.SeedConsumer = rng;
            RngOnlineSeedTimer qrng = new();
            await using (qrng.DynamicContext()) await qrng.SeedAsync().DynamicContext();
            // Initialize the PKI
            if (CliApi.CurrentContext.Arguments["vendorPki", requireValues: true])
            {
                FileStream pkiFile = FsHelper.CreateFileStream(CliApi.CurrentContext.Arguments.Single("vendorPki"), FileMode.Open, FileAccess.Read, FileShare.Read);
                await using (pkiFile.DynamicContext())
                    CryptoEnvironment.PKI = await pkiFile.ReadSerializedAsync<SignedPkiStore>(await pkiFile.ReadSerializerVersionAsync().DynamicContext()).DynamicContext();
            }
            else
            {
                //TODO Use vendor PKI
            }
            CryptoEnvironment.PKI.EnableLocalPki();
        }

        /// <summary>
        /// Read the password
        /// </summary>
        /// <param name="pwdVar">Environment variable name which contains the encryption password to use (if <see langword="null"/>, the password is required to be given from STDIN)</param>
        /// <param name="output">Write something to the console?</param>
        /// <returns>Password</returns>
        public static async Task<byte[]> ReadPasswordAsync(string? pwdVar, bool output)
        {
            await InitCryptoAsync().DynamicContext();
            if (pwdVar is not null)
            {
                // From an environment variable
                if (output) Console.WriteLine("Use an environment variable");
                return Environment.GetEnvironmentVariable(pwdVar)?.GetBytes() ?? throw new ArgumentException("Environment variable not found", nameof(pwdVar));
            }
            else
            {
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
            await InitCryptoAsync().DynamicContext();
            // Apply PBKDF#2
            using SecureByteArrayStructSimple salt = new(pwd.Mac(pwd, new()
            {
                MacAlgorithm = MacHmacSha3_512Algorithm.ALGORITHM_NAME
            }));
            if (output) Console.WriteLine($"First key stretching: {KdfHelper.GetAlgorithm(KdfPbKdf2Algorithm.ALGORITHM_VALUE).DisplayName} with {HashHelper.GetAlgorithm(HashSha3_384Algorithm.ALGORITHM_NAME).DisplayName} hash and 250.000 iterations");
            (byte[] stretchedPwd, _) = pwd.Stretch(HashSha512Algorithm.HASH_LENGTH, salt.Array, new()
            {
                KdfAlgorithm = KdfPbKdf2Algorithm.ALGORITHM_NAME,
                KdfOptions = new KdfPbKdf2Options()
                {
                    HashAlgorithm = HashSha3_384Algorithm.ALGORITHM_NAME
                },
                KdfIterations = 250000
            });
            // Apply Argon2id
            if (output) Console.WriteLine($"Second key stretching: {KdfHelper.GetAlgorithm(KdfArgon2IdAlgorithm.ALGORITHM_VALUE).DisplayName} with 46M memory limit");
            using (SecureByteArrayStructSimple secureStretchedPwd = new(stretchedPwd))
                (stretchedPwd, _) = stretchedPwd.Stretch(HashSha512Algorithm.HASH_LENGTH, salt.Array, new()
                {
                    KdfAlgorithm = KdfArgon2IdAlgorithm.ALGORITHM_NAME,
                    KdfOptions = new KdfArgon2IdOptions()
                    {
                        Parallelism = 1,
                        MemoryLimit = 47104
                    },
                    KdfIterations = 250000// Dummy value (Argon2 doesn't use iterations)
                });
            if (!tpm) return new(stretchedPwd);
            // Apply TPM HMAC
            if (!await Tpm2Helper.IsAvailableAsync().DynamicContext()) throw new ArgumentException("TPM not available", nameof(tpm));
            if (output) Console.WriteLine($"TPM2 HMAC ({Tpm2Helper.GetDigestAlgorithm(Tpm2Helper.GetMaxDigestSize())})");
            using (SecureByteArrayStructSimple secureStretchedPwd = new(stretchedPwd))
                return new(Tpm2Helper.Hmac(secureStretchedPwd.Array, key: pwd));
        }
    }
}

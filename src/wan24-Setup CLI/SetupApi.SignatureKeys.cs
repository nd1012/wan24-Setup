using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Contracts;
using wan24.CLI;
using wan24.Core;
using wan24.Crypto;
using wan24.Crypto.BC;
using wan24.StreamSerializerExtensions;

namespace wan24.Setup.CLI
{
    // Signature keys
    public sealed partial class SetupApi
    {
        /// <summary>
        /// Signature key signing purpose
        /// </summary>
        public const string KEY_SIGNATURE_PURPOSE = "wan24Setup installer package signing permitted public signature key";

        /// <summary>
        /// Detailed help for <see cref="CreateKeyAsync(FileStream, string, string?, bool)"/>
        /// </summary>
        public static string HelpCreateKey => Properties.Resources.CreateKey_Help;

        /// <summary>
        /// Create a private key suite for signing installer packages
        /// </summary>
        /// <param name="file">Key suite filename (file will be overwritten!)</param>
        /// <param name="email">Email address of the key suite owner</param>
        /// <param name="pwdVar">Environment variable name which contains the encryption password to use (if <see langword="null"/>, the password is required to be given from STDIN)</param>
        /// <param name="tpm">Use a TPM in addition to the password?</param>
        [CliApi("createKey", HelpTextProperty = "wan24.Setup.CLI.SetupApi.HelpCreateKey")]
        [DisplayText("Create signature keys")]
        [Description("Creates a private key suite for signing installer packages")]
        [StdIn("Password")]
        [StdOut("Processing informations")]
        public static async Task CreateKeyAsync(

            [CliApiFileStream(Example = "/path/to/key.private", Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None)]
            [DisplayText("Output filename")]
            [Description("Path to the file which will contain the encrypted private key suite")]
            FileStream file,

            [CliApi(Example = "alias@domain.com")]
            [DisplayText("Email address")]
            [Description("Email address of the key suite owner")]
            [EmailAddress, StringLength(byte.MaxValue)]
            string email,

            [CliApi]
            [DisplayText("Password variable")]
            [Description("Environment variable name which contains the encryption password to use (if not given, the password is required to be given from STDIN)")]
            [StringLength(byte.MaxValue)]
            string? pwdVar = null,

            [CliApi]
            [DisplayText("Use TPM")]
            [Description("Use a TPM 2.0 to create a HMAC of the password")]
            bool tpm = false

            )
        {
            Console.WriteLine($"Creating a private signature key suite (output to file \"{file.Name}\") for installer package signing.");
            await InitCryptoAsync().DynamicContext();
            await using (file.DynamicContext())
            {
                // Email address should be lowercase
                email = email.ToLower();
                // Get the encryption password
                Console.WriteLine("Working on your password...");
                using SecureByteArrayStructSimple securePwd = new(await ReadPasswordAsync(pwdVar, output: true).DynamicContext());
                using SecureByteArray finalPwd = await FinalizePasswordAsync(securePwd.Array, tpm, output: true);
                // Create the private key suite
                Console.WriteLine("Creating private key suite...");
                using PrivateKeySuite privateKeys = new();
                CryptoOptions options = new()
                {
                    AsymmetricAlgorithm = AsymmetricEcDsaAlgorithm.ALGORITHM_NAME,
                    AsymmetricKeyBits = AsymmetricEcDsaAlgorithm.Instance.AllowedKeySizes.Last()
                };
                Console.WriteLine($"Primary signature key: {AsymmetricHelper.GetAlgorithm(options.AsymmetricAlgorithm).DisplayName} (key size/ID {options.AsymmetricKeyBits})");
                privateKeys.SignatureKey = AsymmetricHelper.CreateSignatureKeyPair(options);
                options.AsymmetricAlgorithm = AsymmetricDilithiumAlgorithm.ALGORITHM_NAME;
                options.AsymmetricKeyBits = AsymmetricDilithiumAlgorithm.Instance.AllowedKeySizes.Last();
                Console.WriteLine($"Counter signature key: {AsymmetricHelper.GetAlgorithm(options.AsymmetricAlgorithm).DisplayName} (key size/ID {options.AsymmetricKeyBits})");
                privateKeys.CounterSignatureKey = AsymmetricHelper.CreateSignatureKeyPair(options);
                // Encrypt and write the private key suite
                Console.WriteLine("Storing encrypted private key suite...");
                CryptoOptions encryptionOptions = new CryptoOptions()
                    .WithEncryptionAlgorithm(EncryptionSerpent256CbcAlgorithm.ALGORITHM_NAME)
                    .WithMac(MacHmacSha3_512Algorithm.ALGORITHM_NAME);
                Console.WriteLine($"Encryption algorithm: {EncryptionHelper.GetAlgorithm(encryptionOptions.Algorithm!).DisplayName} ({MacHelper.GetAlgorithm(encryptionOptions.MacAlgorithm!).DisplayName} authenticated)");
                using SecureByteArrayStructSimple privateKeysData = new(privateKeys.Encrypt(finalPwd.Array, encryptionOptions));
                await file.WriteAsync(privateKeysData.Array).DynamicContext();
                // Create a KSR
                Console.WriteLine("Creating key signature request...");
                using AsymmetricPublicKeySigningRequest ksr = new(privateKeys.SignatureKey.PublicKey, new()
                {
                    { SignedAttributes.PKI_DOMAIN, "wan24Setup" },
                    { SignedAttributes.OWNER_IDENTIFIER, email },
                    { SignedAttributes.GRANTED_KEY_USAGES, ((int)AsymmetricAlgorithmUsages.Signature).ToString() },
                    { SignedAttributes.SIGNATURE_PUBLIC_KEY_IDENTIFIER, Convert.ToBase64String(privateKeys.SignatureKey.ID) },
                    { SignedAttributes.SIGNATURE_PUBLIC_COUNTER_KEY_IDENTIFIER, Convert.ToBase64String(privateKeys.CounterSignatureKey.ID) }
                }, KEY_PURPOSE);
                ksr.SignRequest(privateKeys.SignatureKey);
                FileStream ksrFile = FsHelper.CreateFileStream($"{file.Name}.ksr", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                await using (ksrFile.DynamicContext())
                    await ksrFile.WriteSerializedAsync(ksr).DynamicContext();
                Console.WriteLine("Done.");
            }
        }

        /// <summary>
        /// Print the KSR informations
        /// </summary>
        /// <param name="file">Path to the KSR file</param>
        /// <returns>Exit code</returns>
        [CliApi("printKsr")]
        [DisplayText("Print KSR contents")]
        [Description("Print the informations of a signature key signing request")]
        [StdOut("KSR contents")]
        [ExitCode(0, "Valid request, ready for signing")]
        [ExitCode(2, "Request invalid")]
        public static async Task<int> PrintKsrAsync(
            [CliApiFileStream(Example = "/path/to/key.ksr", Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read)]
            [DisplayText("KSR file")]
            [Description("Path to the KSR file")]
            FileStream file
            )
        {
            await InitCryptoAsync().DynamicContext();
            await using (file.DynamicContext())
            {
                Console.WriteLine("Printing signature key signing request...");
                using AsymmetricPublicKeySigningRequest ksr = await file.ReadSerializedAsync<AsymmetricPublicKeySigningRequest>().DynamicContext();
                Console.WriteLine($"Key algorithm: {ksr.PublicKey.Algorithm.DisplayName} (key size/ID {ksr.PublicKey.Bits})");
                Console.WriteLine($"Key ID: {Convert.ToBase64String(ksr.PublicKey.ID)}");
                Console.WriteLine("Attributes:");
                foreach (var kvp in ksr.Attributes)
                    Console.WriteLine($"\t{kvp.Key} = {kvp.Value}");
                Console.WriteLine("Signature:");
                Console.WriteLine($"\tSigned: {ksr.Signature?.Signed.ToLocalTime()}");
                Console.WriteLine($"\tHash algorithm: {ksr.Signature?.HashAlgorithm}");
                Console.WriteLine($"\tSigner: {(ksr.Signature is null ? string.Empty : Convert.ToBase64String(ksr.Signature.Signer))}");
                Console.WriteLine($"\tPurpose: {ksr.Signature?.Purpose}");
                int exitCode = 0;
                try
                {
                    ksr.ValidateRequestSignature();
                    Console.WriteLine("\tSignature valid");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\tSignature validation failed: {ex.Message}");
                    exitCode = 2;
                }
                //TODO Validate request in detail
                return exitCode;
            }
        }

        /// <summary>
        /// Sign a signature key
        /// </summary>
        /// <param name="file">Signature key signing request (KSR) file path</param>
        /// <param name="output">Signed public key filepath</param>
        /// <param name="privateKeys">Private key suite file path</param>
        /// <param name="pwdVar">Environment variable name which contains the encryption password to use (if <see langword="null"/>, the password is required to be given from STDIN)</param>
        [CliApi("signKey")]
        [DisplayText("Sign a KSR")]
        [Description("Signs a public signature key (KSR)")]
        [StdIn("Password")]
        [StdOut("Processing informations")]
        public static async Task SignKeyAsync(

            [CliApiFileStream(Example = "/path/to/publicKey.ksr", Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read)]
            [DisplayText("KSR filename")]
            [Description("Path to the KSR file")]
            FileStream file,

            [CliApiFileStream(Example = "/path/to/signedKey.public", Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None)]
            [DisplayText("Public key filename")]
            [Description("Path to the signed public key file")]
            FileStream output,

            [CliApiFileStream(Example = "/path/to/vendorSignatureKey.private", Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read)]
            [DisplayText("Private key filename")]
            [Description("Path to the private signature key suite file")]
            FileStream privateKeys,

            [CliApi]
            [DisplayText("Password variable")]
            [Description("Environment variable name which contains the decryption password to use (if not given, the password is required to be given from STDIN)")]
            [StringLength(byte.MaxValue)]
            string? pwdVar = null

            )
        {
            Console.WriteLine("Processing KSR...");
            await InitCryptoAsync().DynamicContext();
            string ksrFileName = file.Name;
            try
            {
                if (await PrintKsrAsync(file).DynamicContext() != 0) throw new CliArgException("KSR invalid", nameof(file));
                file = FsHelper.CreateFileStream(ksrFileName, FileMode.Open, FileAccess.Read, FileShare.Read);
                using AsymmetricPublicKeySigningRequest ksr = await file.ReadSerializedAsync<AsymmetricPublicKeySigningRequest>().DynamicContext();
                Console.WriteLine("Working on the password...");
                using SecureByteArrayStructSimple pwd = new(await ReadPasswordAsync(pwdVar, output: true).DynamicContext());
                Console.WriteLine("Loading private keys...");
                using SecureByteArrayStructSimple privateKeysData = new((int)privateKeys.Length);
                await privateKeys.ReadAsync(privateKeysData.Memory).DynamicContext();
                using PrivateKeySuite privateKeysSuite = PrivateKeySuite.Decrypt(privateKeysData.Array, pwd.Array);
                Contract.Assert(
                    privateKeysSuite.SignatureKey is not null && 
                    privateKeysSuite.CounterSignatureKey is not null && 
                    privateKeysSuite.SignedPublicKey is not null && 
                    privateKeysSuite.SignedPublicCounterKey is not null
                    );
                Console.WriteLine("Signing public key...");
                using AsymmetricSignedPublicKey publicKey = ksr.GetAsUnsignedKey();
                publicKey.Sign(
                    privateKeysSuite.SignatureKey, 
                    privateKeysSuite.SignedPublicKey, 
                    privateKeysSuite.CounterSignatureKey, 
                    privateKeysSuite.SignedPublicCounterKey, 
                    KEY_SIGNATURE_PURPOSE, 
                    new()
                    {
                        HashAlgorithm = HashBcSha3_512Algorithm.ALGORITHM_NAME
                    }
                    );
                await output.WriteSerializerVersionAsync().DynamicContext();
                await output.WriteSerializedAsync(publicKey).DynamicContext();
                Console.WriteLine("Done.");
            }
            finally
            {
                await file.DisposeAsync().DynamicContext();
                await output.DisposeAsync().DynamicContext();
                await privateKeys.DisposeAsync().DynamicContext();
            }
        }
    }
}

using System.ComponentModel;
using wan24.CLI;
using wan24.Core;

namespace wan24.Setup.CLI
{
    /// <summary>
    /// Setup API
    /// </summary>
    [CliApi("setup")]
    [DisplayText("Setup API")]
    [Description("wan24-Setup helper CLI")]
    public sealed partial class SetupApi
    {
        /// <summary>
        /// Installer package signature key purpose
        /// </summary>
        public const string KEY_PURPOSE = "wan24Setup installer package signing";
        /// <summary>
        /// Installer package signature purpose
        /// </summary>
        public const string SIGNATURE_PURPOSE = "wan24Setup installer package signature";

        /// <summary>
        /// Constructor
        /// </summary>
        public SetupApi() { }
    }
}

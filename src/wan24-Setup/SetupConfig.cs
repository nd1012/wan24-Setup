namespace wan24.Setup
{
    /// <summary>
    /// Setup configuration
    /// </summary>
    public record class SetupConfig
    {
        /// <summary>
        /// Constructor
        /// </summary>
        public SetupConfig() { }

        /// <summary>
        /// Command to execute
        /// </summary>
        public required string Command { get; init; }

        /// <summary>
        /// Command arguments
        /// </summary>
        public string? Arguments { get; init; }

        /// <summary>
        /// Is an exit of the calling app required?
        /// </summary>
        public bool ExitRequired { get; init; }

        /// <summary>
        /// Administrator privileges required?
        /// </summary>
        public bool RequireAdministratorPrivileges { get; init; }

        /// <summary>
        /// Hide the setup process window?
        /// </summary>
        public bool HideWindow { get; init; }
    }
}

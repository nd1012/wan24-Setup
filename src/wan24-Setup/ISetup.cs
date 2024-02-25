namespace wan24.Setup
{
    /// <summary>
    /// Interface for a setup (may be disposable)
    /// </summary>
    public interface ISetup
    {
        /// <summary>
        /// Run the setup
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Exit code</returns>
        public Task<int> RunAsync(CancellationToken cancellationToken);
    }
}

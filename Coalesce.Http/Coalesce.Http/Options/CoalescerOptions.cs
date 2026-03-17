namespace Coalesce.Http.Coalesce.Http.Options;

public class CoalescerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum time a coalesced waiter will wait for the winner's response
    /// before falling back to an independent request. <see langword="null"/> means no timeout (wait indefinitely).
    /// </summary>
    public TimeSpan? CoalescingTimeout { get; set; }
}

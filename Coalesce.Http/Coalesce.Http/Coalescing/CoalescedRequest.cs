namespace Coalesce.Http.Coalesce.Http.Coalescing;

public sealed partial class RequestCoalescer
{
    private sealed class CoalescedRequest
    {
        /// <summary>
        /// Gets the task completion source that represents the asynchronous operation's completion status.
        /// </summary>
        /// <remarks>Use this property to signal the completion of an asynchronous HTTP operation and to
        /// retrieve the resulting HttpResponseMessage when the operation finishes. The TaskCompletionSource is
        /// configured to run continuations asynchronously to help prevent potential deadlocks in asynchronous
        /// workflows.</remarks>
        public TaskCompletionSource<CachedResponse> Tcs { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

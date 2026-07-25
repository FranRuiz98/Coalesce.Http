using Stampede.Http.Coalescing;
using Stampede.Http.Options;
using FluentAssertions;
using System.Net;

namespace Stampede.Http.Tests.Coalescing;

/// <summary>
/// Verifies that <c>MaxResponseBodyBytes</c> is enforced while the body is being read rather than after it has
/// been fully materialised, so an oversized response never gets allocated in full.
/// </summary>
public sealed class BoundedBodyReadTests
{
    [Fact]
    public async Task DeclaredContentLengthOverLimit_RejectedWithoutReadingTheBody()
    {
        TrackingContent content = new(new byte[1024], declareLength: true);
        RequestCoalescer coalescer = new(new CoalescerOptions { MaxResponseBodyBytes = 10 });

        Func<Task> act = () => coalescer.ExecuteAsync(
            new RequestKey("GET", "https://api.test/big"),
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*MaxResponseBodyBytes*");

        content.BytesRead.Should().Be(0,
            "a declared Content-Length over the limit must be rejected before any of the body is read");
    }

    [Fact]
    public async Task ChunkedBodyOverLimit_AbandonedPartWayThrough()
    {
        // No Content-Length (chunked): the limit can only be enforced while streaming. The read must stop
        // shortly after crossing the limit instead of buffering all 4 MB.
        TrackingContent content = new(new byte[4 * 1024 * 1024], declareLength: false);
        RequestCoalescer coalescer = new(new CoalescerOptions { MaxResponseBodyBytes = 1024 });

        Func<Task> act = () => coalescer.ExecuteAsync(
            new RequestKey("GET", "https://api.test/chunked"),
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*MaxResponseBodyBytes*");

        content.BytesRead.Should().BeLessThan(4 * 1024 * 1024,
            "an oversized chunked body must be abandoned mid-stream, not buffered in full");
    }

    [Fact]
    public async Task BodyWithinLimit_ReadInFull()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        TrackingContent content = new(payload, declareLength: true);
        RequestCoalescer coalescer = new(new CoalescerOptions { MaxResponseBodyBytes = 1024 });

        HttpResponseMessage response = await coalescer.ExecuteAsync(
            new RequestKey("GET", "https://api.test/small"),
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }),
            TestContext.Current.CancellationToken);

        byte[] received = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        received.Should().Equal(payload);
    }

    [Fact]
    public async Task ChunkedBodyWithinLimit_ReadInFull()
    {
        byte[] payload = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i % 256))];
        TrackingContent content = new(payload, declareLength: false);
        RequestCoalescer coalescer = new(new CoalescerOptions { MaxResponseBodyBytes = 1024 * 1024 });

        HttpResponseMessage response = await coalescer.ExecuteAsync(
            new RequestKey("GET", "https://api.test/chunked-ok"),
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }),
            TestContext.Current.CancellationToken);

        byte[] received = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        received.Should().Equal(payload, "a chunked body within the limit must be reassembled exactly");
    }

    /// <summary>
    /// An <see cref="HttpContent"/> that records how much of its payload was actually read and can hide its
    /// length to emulate a chunked response.
    /// </summary>
    private sealed class TrackingContent : HttpContent
    {
        private readonly byte[] _payload;
        private readonly bool _declareLength;
        private int _bytesRead;

        public TrackingContent(byte[] payload, bool declareLength)
        {
            _payload = payload;
            _declareLength = declareLength;

            if (declareLength)
            {
                Headers.ContentLength = payload.Length;
            }
        }

        public int BytesRead => Volatile.Read(ref _bytesRead);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            CreateContentReadStreamAsync().ContinueWith(t => t.Result.CopyToAsync(stream)).Unwrap();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CountingStream(_payload, count => Interlocked.Add(ref _bytesRead, count)));

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return _declareLength;
        }

        /// <summary>A read-only stream over the payload that reports how many bytes were consumed.</summary>
        private sealed class CountingStream(byte[] payload, Action<int> onRead) : Stream
        {
            private int _position;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => payload.Length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                int remaining = payload.Length - _position;
                if (remaining <= 0)
                {
                    return 0;
                }

                int toCopy = Math.Min(remaining, buffer.Length);
                payload.AsSpan(_position, toCopy).CopyTo(buffer[..toCopy]);
                _position += toCopy;
                onRead(toCopy);

                return toCopy;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                new(Read(buffer.Span));

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Task.FromResult(Read(buffer.AsSpan(offset, count)));

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

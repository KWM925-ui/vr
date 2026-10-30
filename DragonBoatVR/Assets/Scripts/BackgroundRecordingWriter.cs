using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace DragonBoatVR
{
    /// <summary>FIFO recording sink. Only the worker touches the supplied writer after construction.
    /// A completed Dispose drains and durably flushes; a failed/timeout Dispose is not completion.</summary>
    public sealed class BackgroundRecordingWriter : StreamWriter
    {
        const int CloseTimeoutMilliseconds = 10000;
        const int FlushBatchBytes = 64 * 1024;
        const int FlushIntervalMilliseconds = 50;
        readonly object _gate = new object();
        readonly Queue<Line> _queue = new Queue<Line>();
        readonly StreamWriter _sink;
        readonly Thread _worker;
        readonly int _maximumQueuedBytes;
        readonly Encoding _encoding;
        readonly string _lineEnding;
        readonly string _headerJson;
        RecordingCompletion _completion;
        long _queuedBytes, _writtenLines;
        double _maximumWriteSeconds;
        bool _closing, _disposed;
        volatile bool _completed;
        IOException _failure;

        sealed class Line
        {
            public string value;
            public int bytes;
        }

        public long QueuedBytes { get { lock (_gate) return _queuedBytes; } }
        public long WrittenLines { get { lock (_gate) return _writtenLines; } }
        public double MaximumWriteSeconds { get { lock (_gate) return _maximumWriteSeconds; } }
        public bool HasCompleted => _completed;
        /// <summary>Start a durable drain without blocking the Unity thread. Completion
        /// must still be observed and Dispose called to surface any I/O failure.</summary>
        public void BeginClose()
        {
            lock (_gate) { _closing = true; Monitor.PulseAll(_gate); }
        }

        public BackgroundRecordingWriter(StreamWriter sink, int maxQueuedBytes = 4 * 1024 * 1024, string headerJson = null)
            : base(Stream.Null, new UTF8Encoding(false), 1024, true)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            if (maxQueuedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));
            _sink = sink;
            _maximumQueuedBytes = maxQueuedBytes;
            _encoding = sink.Encoding;
            _lineEnding = sink.NewLine;
            _headerJson = headerJson;
            _worker = new Thread(WriteQueuedLines) { IsBackground = true, Name = "DragonBoat recording writer" };
            _worker.Start();
        }

        public override void WriteLine(string value)
        {
            int bytes;
            try { bytes = checked(_encoding.GetByteCount(value ?? string.Empty) + _encoding.GetByteCount(_lineEnding)); }
            catch (Exception error) when (error is EncoderFallbackException || error is OverflowException)
            {
                lock (_gate)
                {
                    SaveFailure("Recording line could not be encoded.", error);
                    _closing = true; Monitor.PulseAll(_gate);
                    throw _failure;
                }
            }
            lock (_gate)
            {
                ThrowFailure();
                if (_closing || _disposed) throw new ObjectDisposedException(nameof(BackgroundRecordingWriter));
                // Include the line already being written. Slow I/O cannot evade the capacity.
                if (bytes > _maximumQueuedBytes - _queuedBytes)
                {
                    SaveFailure("Recording queue exceeded its byte limit; recording is incomplete.", null);
                    _closing = true; Monitor.PulseAll(_gate);
                    throw _failure;
                }
                _queue.Enqueue(new Line { value = value, bytes = bytes });
                _queuedBytes += bytes;
                Monitor.PulseAll(_gate);
            }
        }

        // A per-frame caller never waits for disk. Errors from completed worker operations
        // surface on the next WriteLine/Flush, and Dispose always checks them after draining.
        public override void Flush()
        {
            lock (_gate)
            {
                ThrowFailure();
                if (_disposed) throw new ObjectDisposedException(nameof(BackgroundRecordingWriter));
            }
        }

        // Called only after the final physical frame. The worker verifies its completed
        // frame count and creates the manifest after draining; the caller cannot supply a hash.
        public void CompleteRecording(int frameCount, double lastFrameTimeSeconds, string signature)
        {
            if (frameCount <= 0 || double.IsNaN(lastFrameTimeSeconds) || double.IsInfinity(lastFrameTimeSeconds)
                || string.IsNullOrEmpty(signature) || _headerJson == null)
                throw new IOException("Invalid recording completion request.");
            lock (_gate)
            {
                ThrowFailure();
                if (_closing || _disposed) throw new ObjectDisposedException(nameof(BackgroundRecordingWriter));
                _completion = new RecordingCompletion { frameCount = frameCount,
                    lastFrameTimeSeconds = lastFrameTimeSeconds, configurationSignature = signature };
                _closing = true; Monitor.PulseAll(_gate);
            }
        }

        void ThrowFailure() { if (_failure != null) throw _failure; }
        void SaveFailure(string message, Exception error)
        {
            if (_failure == null) _failure = error == null ? new IOException(message) : new IOException(message, error);
        }

        void WriteQueuedLines()
        {
            RecordingContentHash contentHash = null;
            int bufferedBytes = 0;
            long flushStarted = Stopwatch.GetTimestamp();
            try
            {
                if (_headerJson != null)
                {
                    contentHash = new RecordingContentHash();
                    contentHash.Append(_headerJson);
                }
                while (true)
                {
                    Line line = null;
                    lock (_gate)
                    {
                        while (_queue.Count == 0 && !_closing)
                        {
                            if (bufferedBytes == 0) Monitor.Wait(_gate);
                            else
                            {
                                int remaining = FlushIntervalMilliseconds - (int)((Stopwatch.GetTimestamp() - flushStarted)
                                    * 1000.0 / Stopwatch.Frequency);
                                if (remaining <= 0) break;
                                Monitor.Wait(_gate, remaining);
                            }
                        }
                        if (_queue.Count == 0 && _closing) break;
                        if (_queue.Count > 0) line = _queue.Dequeue();
                    }
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        if (line != null)
                        {
                            _sink.WriteLine(line.value);
                            contentHash?.Append(line.value);
                            if (bufferedBytes == 0) flushStarted = started;
                            bufferedBytes += line.bytes;
                            lock (_gate) _writtenLines++;
                        }
                        // Per-line flushing defeated StreamWriter/FileStream buffering
                        // at several MB/s of physics/render evidence. Bound visibility
                        // latency without making the producer wait or enlarging its queue.
                        if (bufferedBytes >= FlushBatchBytes || bufferedBytes > 0
                            && (Stopwatch.GetTimestamp() - flushStarted) * 1000.0 / Stopwatch.Frequency >= FlushIntervalMilliseconds)
                        {
                            _sink.Flush();
                            bufferedBytes = 0;
                        }
                    }
                    finally
                    {
                        double elapsed = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
                        lock (_gate)
                        {
                            _maximumWriteSeconds = Math.Max(_maximumWriteSeconds, elapsed);
                            if (line != null) _queuedBytes -= line.bytes;
                        }
                    }
                }
                RecordingCompletion completion;
                lock (_gate) completion = _failure == null ? _completion : null;
                if (completion != null)
                {
                    if (_writtenLines != completion.frameCount)
                        throw new IOException("Recording completion frame count differs after drain.");
                    completion.contentSha256 = contentHash.FinishHex();
                    // JSON construction is pure .NET; no Unity APIs run on the I/O worker.
                    _sink.WriteLine(completion.ToJson());
                }
                _sink.Flush();
                if (_sink.BaseStream is FileStream file) file.Flush(true);
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    SaveFailure("Background recording write failed; recording is incomplete.", error);
                    _closing = true; _queue.Clear(); _queuedBytes = 0;
                }
            }
            finally
            {
                contentHash?.Dispose();
                try { _sink.Dispose(); }
                catch (Exception error)
                {
                    lock (_gate) SaveFailure("Background recording close failed; recording is incomplete.", error);
                }
                finally { _completed = true; }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            // The worker owns the sink. Timeout must not close a stream under an ongoing write.
            lock (_gate) { _closing = true; Monitor.PulseAll(_gate); }
            bool drained = _worker.Join(CloseTimeoutMilliseconds);
            IOException failure;
            lock (_gate)
            {
                if (!drained) SaveFailure("Background recording close timed out; recording is incomplete.", null);
                _disposed = true;
                failure = _failure;
            }
            base.Dispose(true);
            if (failure != null) throw failure;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Controlled I/O sinks exercise actual worker ownership and failure paths.</summary>
    public static class BackgroundRecordingWriterSelfTest
    {
        sealed class Sink : StreamWriter
        {
            public readonly ManualResetEvent Entered = new ManualResetEvent(false);
            public readonly ManualResetEvent Release = new ManualResetEvent(false);
            public readonly ManualResetEvent Disposed = new ManualResetEvent(false);
            public readonly ManualResetEvent CallerComplete = new ManualResetEvent(false);
            public readonly List<string> Lines = new List<string>();
            public bool block, failWrite, failFlush, failDispose;
            public int workerThread, disposeThread, flushCalls;
            public Sink() : base(new MemoryStream(), new UTF8Encoding(false)) { NewLine = "\n"; }
            public override void WriteLine(string value)
            {
                workerThread = Thread.CurrentThread.ManagedThreadId;
                Entered.Set();
                if (block && !Release.WaitOne(20000)) throw new IOException("Self-test blocked sink safety deadline.");
                if (failWrite) throw new IOException("Injected write failure.");
                lock (Lines) Lines.Add(value);
            }
            public override void Flush()
            {
                Interlocked.Increment(ref flushCalls);
                if (failFlush) throw new IOException("Injected flush failure.");
            }
            protected override void Dispose(bool disposing)
            {
                if (!disposing) return;
                disposeThread = Thread.CurrentThread.ManagedThreadId;
                try { base.Dispose(true); }
                finally { Disposed.Set(); }
                if (failDispose) throw new IOException("Injected close failure.");
            }
            public void CleanupSignals()
            {
                Entered.Dispose(); Release.Dispose(); Disposed.Dispose(); CallerComplete.Dispose();
            }
        }
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static bool IoFailure(Action action)
        { try { action(); return false; } catch (IOException) { return true; } }
        static void Cleanup(BackgroundRecordingWriter writer, Sink sink)
        {
            sink.Release.Set();
            try { writer.Dispose(); } catch (IOException) { }
            Require(sink.Disposed.WaitOne(3000), "Worker did not release its sink during cleanup.");
            sink.CleanupSignals();
        }

        public static bool CheckAll()
        {
            CheckBlockedFifo();
            CheckBatchedDrain();
            CheckCapacityAndPermanentFailure();
            CheckWorkerFailures();
            CheckCloseTimeoutOwnership();
            UnityEngine.Debug.Log("[CHAIN] background writer controlled sink FIFO/capacity/failure/close/timeout ownership=True");
            return true;
        }
        static void CheckBatchedDrain()
        {
            var sink = new Sink { block = true };
            var writer = new BackgroundRecordingWriter(sink);
            const int count = 256;
            try
            {
                writer.WriteLine("0");
                Require(sink.Entered.WaitOne(3000), "Batch fixture did not block its first write.");
                for (int i = 1; i < count; i++) writer.WriteLine(i.ToString());
                sink.Release.Set(); writer.Dispose();
                Require(writer.HasCompleted && writer.WrittenLines == count && writer.QueuedBytes == 0,
                    "Batch drain lost records or claimed completion early.");
                lock (sink.Lines)
                    for (int i = 0; i < count; i++) Require(sink.Lines[i] == i.ToString(), "Batch drain reordered records.");
                Require(sink.flushCalls < count / 4, "Batch drain still flushes almost every record.");
            }
            finally { Cleanup(writer, sink); }
        }
        static void CheckBlockedFifo()
        {
            var sink = new Sink { block = true };
            var writer = new BackgroundRecordingWriter(sink);
            Exception producerError = null;
            bool producerStarted = false;
            var producer = new Thread(() =>
            {
                try { writer.WriteLine("second"); writer.Flush(); }
                catch (Exception error) { producerError = error; }
                finally { sink.CallerComplete.Set(); }
            }) { IsBackground = true };
            try
            {
                writer.WriteLine("first");
                Require(sink.Entered.WaitOne(3000), "Worker never reached the blocked sink.");
                producer.Start(); producerStarted = true;
                // The worker is still blocked. Completion is evidence the producer did not await I/O.
                Require(sink.CallerComplete.WaitOne(2000), "Enqueue/Flush blocked on the disk sink.");
                Require(producerError == null, "Producer failed instead of completing its enqueue/Flush.");
                Require(!sink.Disposed.WaitOne(0), "Caller disposed an active sink.");
                writer.BeginClose();
                Require(!writer.HasCompleted, "A blocked drain claimed durable completion.");
                sink.Release.Set(); writer.Dispose();
                Require(writer.HasCompleted && sink.Disposed.WaitOne(0), "Completion preceded sink disposal.");
                lock (sink.Lines) Require(sink.Lines.Count == 2 && sink.Lines[0] == "first" && sink.Lines[1] == "second", "FIFO/drain differs.");
                Require(writer.QueuedBytes == 0 && writer.WrittenLines == 2 && writer.MaximumWriteSeconds > 0,
                    "Completion metrics do not describe completed writes.");
                Require(sink.workerThread == sink.disposeThread && sink.disposeThread != Thread.CurrentThread.ManagedThreadId,
                    "Worker must own write, flush and close.");
                bool disposed = false; try { writer.WriteLine("after-close"); } catch (ObjectDisposedException) { disposed = true; }
                Require(disposed, "Closed writer accepted another line.");
            }
            finally
            {
                sink.Release.Set();
                if (producerStarted) producer.Join(3000);
                Cleanup(writer, sink);
            }
        }
        static void CheckCapacityAndPermanentFailure()
        {
            var sink = new Sink { block = true };
            var writer = new BackgroundRecordingWriter(sink, 4);
            try
            {
                writer.WriteLine("中"); // Three UTF-8 bytes plus one newline fills the limit.
                Require(sink.Entered.WaitOne(3000), "No in-flight blocked line.");
                Require(writer.QueuedBytes == 4, "In-flight UTF-8 bytes disappeared from capacity.");
                Require(IoFailure(() => writer.WriteLine("")), "An in-flight line failed to bound queue capacity.");
                Require(IoFailure(writer.Flush) && IoFailure(() => writer.WriteLine("")), "Overflow did not permanently reject input.");
                sink.Release.Set();
                Require(IoFailure(writer.Dispose), "Overflow was published as a complete recording.");
                lock (sink.Lines) Require(sink.Lines.Count == 1 && sink.Lines[0] == "中", "Overflow dropped an already accepted line or wrote rejected data.");
                Require(writer.WrittenLines == 1 && writer.QueuedBytes == 0 && sink.Disposed.WaitOne(0), "Overflow drain/close incomplete.");
                Require(IoFailure(writer.Flush), "Overflow error disappeared after capacity returned.");
            }
            finally { Cleanup(writer, sink); }
        }
        static void CheckWorkerFailures()
        {
            for (int kind = 0; kind < 3; kind++)
            {
                var sink = new Sink { failWrite = kind == 0, failFlush = kind == 1, failDispose = kind == 2 };
                var writer = new BackgroundRecordingWriter(sink);
                try
                {
                    writer.WriteLine("failure-test");
                    if (kind != 2)
                    {
                        Require(sink.Disposed.WaitOne(3000), "Worker write/flush error did not close the sink.");
                        Require(IoFailure(writer.Flush) && IoFailure(() => writer.WriteLine("must-reject")), "Worker failure did not reach producer.");
                    }
                    Require(IoFailure(writer.Dispose), "Worker write/flush/dispose error did not propagate at close.");
                    Require(sink.Disposed.WaitOne(0), "Failed close leaked sink ownership.");
                }
                finally { Cleanup(writer, sink); }
            }
        }
        static void CheckCloseTimeoutOwnership()
        {
            var sink = new Sink { block = true };
            var writer = new BackgroundRecordingWriter(sink);
            try
            {
                writer.WriteLine("blocked-close");
                Require(sink.Entered.WaitOne(3000), "Worker never entered close-timeout sink.");
                var elapsed = Stopwatch.StartNew();
                Require(IoFailure(writer.Dispose), "A blocked worker close was reported complete.");
                elapsed.Stop();
                Require(elapsed.Elapsed.TotalSeconds >= 9 && elapsed.Elapsed.TotalSeconds < 15, "Close timeout did not bound the caller wait at about ten seconds.");
                Require(!sink.Disposed.WaitOne(0) && writer.QueuedBytes > 0, "Timeout stole sink ownership or marked in-flight work complete.");
                Require(IoFailure(writer.Flush), "Close-timeout error was lost.");
                sink.Release.Set();
                Require(sink.Disposed.WaitOne(3000), "Released timeout worker did not close its sink.");
                Require(IoFailure(writer.Dispose), "Late completion erased the previous timeout/incomplete result.");
                Require(writer.WrittenLines == 1 && writer.QueuedBytes == 0 && sink.disposeThread == sink.workerThread,
                    "Late drain/ownership differs.");
            }
            finally { Cleanup(writer, sink); }
        }
    }
}

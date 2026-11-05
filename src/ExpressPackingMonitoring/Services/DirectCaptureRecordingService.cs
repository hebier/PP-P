#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services
{
    public sealed record SegmentInfo(string Path, DateTimeOffset StartUtc, TimeSpan Length)
    {
        public DateTimeOffset EndUtc => StartUtc + Length;
    }

    /// <summary>
    /// PP-PATCH: real-time main path. Opens the UVC camera ONCE with ffmpeg and keeps it running.
    ///
    /// HARD RULE: the main path touches NO pixels. Both outputs are `-c:v copy`:
    ///   (1) local SSD rolling MKV segments (the recording)
    ///   (2) pipe:1 raw MJPEG bytes for the preview bypass
    /// Adding fps=/scale=/drawtext= to this process would break stream-copy and bring back
    /// the 332 MB/s BGR24 traffic that makes the app stutter. Do not do it.
    ///
    /// This class is self-contained: it references no ViewModel, ScanRecord or database types,
    /// so upstream refactors cannot break it.
    /// </summary>
    public sealed class DirectCaptureRecordingService : IDisposable
    {
        private readonly PatchOptions _opt;
        private readonly string _ffmpeg;
        private readonly Channel<byte[]> _frames;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _procGate = new object();

        private Process? _proc;
        private Task? _stdoutTask;
        private Task? _stderrTask;
        private Task? _dispatchTask;
        private Task? _watchTask;
        private volatile bool _stopping;
        private int _frameSequence;
        private long _droppedFrames;
        private int _stderrLines;

        /// <summary>Complete JPEG frames (FFD8..FFD9), delivered on a background task, never on the stdout reader.</summary>
        public event Action<byte[]>? JpegFrame;

        public string RawDir => _opt.ResolveRawSegmentDir();
        public bool IsRunning => _proc is { HasExited: false };
        public long DroppedPreviewFrames => Interlocked.Read(ref _droppedFrames);
        public int PreviewTakeEvery => _opt.GetPreviewProfile().TakeEvery;

        public DirectCaptureRecordingService(PatchOptions opt)
        {
            _opt = opt ?? throw new ArgumentNullException(nameof(opt));
            _ffmpeg = AppPaths.FindFFmpeg()
                      ?? throw new InvalidOperationException("ffmpeg.exe not found (AppPaths.FindFFmpeg)");
            _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
        }

        public void Start()
        {
            Directory.CreateDirectory(RawDir);
            Spawn();
            _dispatchTask = Task.Run(DispatchLoopAsync, CancellationToken.None);
            _watchTask = Task.Run(WatchLoopAsync, CancellationToken.None);
            RuntimeLog.Info("PP-PATCH", $"capture started: {_opt.Width}x{_opt.Height}@{_opt.Fps}, dir={RawDir}");
        }

        private void Spawn()
        {
            lock (_procGate)
            {
                var psi = new ProcessStartInfo(_ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,   // used only to send 'q' for a graceful flush
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // stdout is BINARY MJPEG - read BaseStream directly, never a StreamReader.
                    StandardErrorEncoding = Encoding.UTF8
                };

                // Segment names must be UTC. strftime on Windows honours TZ via the CRT.
                // TODO(field): verify on the target PC that roll_*.mkv names are UTC, not local time.
                psi.Environment["TZ"] = "UTC";

                var a = psi.ArgumentList;
                a.Add("-hide_banner"); a.Add("-loglevel"); a.Add("warning"); a.Add("-y");
                a.Add("-f"); a.Add("dshow");
                a.Add("-rtbufsize"); a.Add("256M");
                a.Add("-vcodec"); a.Add("mjpeg");
                a.Add("-video_size"); a.Add($"{_opt.Width}x{_opt.Height}");
                a.Add("-framerate"); a.Add(_opt.Fps.ToString(CultureInfo.InvariantCulture));
                a.Add("-i"); a.Add($"video={_opt.CameraName}");

                // main path: stream copy to rolling segments
                a.Add("-map"); a.Add("0:v:0");
                a.Add("-c:v"); a.Add("copy");
                a.Add("-f"); a.Add("segment");
                a.Add("-segment_time"); a.Add(_opt.SegmentSeconds.ToString(CultureInfo.InvariantCulture));
                a.Add("-segment_atclocktime"); a.Add("1");
                a.Add("-strftime"); a.Add("1");
                a.Add("-segment_format"); a.Add("mkv");
                a.Add(Path.Combine(RawDir, "roll_%Y%m%dT%H%M%SZ.mkv"));

                // bypass: the same compressed bytes, no filter, no decode
                a.Add("-map"); a.Add("0:v:0");
                a.Add("-c:v"); a.Add("copy");
                a.Add("-f"); a.Add("mjpeg");
                a.Add("pipe:1");

                var p = Process.Start(psi)
                        ?? throw new InvalidOperationException("Failed to start ffmpeg for direct capture");

                _proc = p;
                _stdoutTask = Task.Run(() => StdoutLoop(p.StandardOutput.BaseStream));
                _stderrTask = Task.Run(() => StderrLoop(p.StandardError));
            }
        }

        /// <summary>
        /// Byte-level JPEG framing on the raw stdout stream. State carried across Read() calls
        /// so an SOI/EOI marker split across buffer boundaries is still detected.
        /// NEVER decode, never Invoke UI here: this task must keep draining the pipe.
        /// </summary>
        private void StdoutLoop(Stream stream)
        {
            var buf = new byte[64 * 1024];
            using var ms = new MemoryStream(256 * 1024);
            byte prev = 0;
            bool inFrame = false;

            try
            {
                int n;
                while (!_cts.IsCancellationRequested && (n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        byte b = buf[i];
                        if (!inFrame)
                        {
                            if (prev == 0xFF && b == 0xD8)
                            {
                                ms.SetLength(0);
                                ms.WriteByte(0xFF);
                                ms.WriteByte(0xD8);
                                inFrame = true;
                            }
                        }
                        else
                        {
                            ms.WriteByte(b);
                            if (prev == 0xFF && b == 0xD9)
                            {
                                Emit(ms.ToArray());
                                ms.SetLength(0);
                                inFrame = false;
                            }
                        }
                        prev = b;
                    }
                }
            }
            catch (IOException) { /* process exited */ }
            catch (ObjectDisposedException) { /* stopping */ }
            catch (Exception ex)
            {
                RuntimeLog.Error("PP-PATCH", "stdout framing loop failed", ex);
            }
        }

        private void Emit(byte[] jpeg)
        {
            int seq = Interlocked.Increment(ref _frameSequence);
            if (seq % Math.Max(1, PreviewTakeEvery) != 0) return;   // 30 -> 15 fps without any ffmpeg filter
            if (!_frames.Writer.TryWrite(jpeg))
                Interlocked.Increment(ref _droppedFrames);            // bounded queue: drop OLDEST, never block
        }

        private async Task DispatchLoopAsync()
        {
            try
            {
                await foreach (var jpeg in _frames.Reader.ReadAllAsync(_cts.Token))
                {
                    try { JpegFrame?.Invoke(jpeg); }
                    catch (Exception ex) { RuntimeLog.Warn("PP-PATCH", $"preview consumer failed: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void StderrLoop(StreamReader reader)
        {
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    // ffmpeg warnings can be chatty; keep the first 200 lines then go silent.
                    if (Interlocked.Increment(ref _stderrLines) <= 200)
                        RuntimeLog.Warn("PP-CAPTURE", line);
                }
            }
            catch { /* process exited */ }
        }

        /// <summary>
        /// If ffmpeg dies (USB hiccup, device reset) restart it. New segments continue writing;
        /// the blind window is logged. NOTE: we never use the old "WaitForExit(15000) then Kill"
        /// stop path - that is exactly what truncated the original recordings.
        /// </summary>
        private async Task WatchLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(2000, _cts.Token); }
                catch (OperationCanceledException) { return; }

                if (_stopping) return;
                Process? p = _proc;
                if (p != null && !p.HasExited) continue;

                RuntimeLog.Warn("PP-PATCH", "ffmpeg capture exited unexpectedly - restarting (gap logged)");
                try { Spawn(); }
                catch (Exception ex)
                {
                    RuntimeLog.Error("PP-PATCH", "capture restart failed", ex);
                    await Task.Delay(5000);
                }
            }
        }

        /// <summary>Rolling segments overlapping [fromUtc, toUtc), ordered by UTC start.</summary>
        public IReadOnlyList<SegmentInfo> FindSegments(DateTimeOffset fromUtc, DateTimeOffset toUtc)
        {
            var result = new List<SegmentInfo>();
            if (!Directory.Exists(RawDir)) return result;

            foreach (var file in Directory.EnumerateFiles(RawDir, "roll_*.mkv"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith("roll_", StringComparison.Ordinal)) continue;
                if (!DateTimeOffset.TryParseExact(
                        name.Substring(5),
                        "yyyyMMdd'T'HHmmss'Z'",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var start)) continue;

                var length = TimeSpan.FromSeconds(_opt.SegmentSeconds);
                if (start + length > fromUtc && start < toUtc)
                    result.Add(new SegmentInfo(file, start, length));
            }
            return result.OrderBy(x => x.StartUtc).ToList();
        }

        /// <summary>
        /// Delete old rolling segments. Never deletes anything still referenced by an in-flight
        /// clip or by the archive queue. Call this only after the queue has been drained.
        /// </summary>
        public int CleanupOldSegments(ISet<string>? protectedPaths = null)
        {
            // RawRetentionHours means "hours to keep". A 48 h floor covers any in-flight clip
            // plus the 24 h archive safety net, so reference counting is unnecessary here.
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(Math.Max(48, _opt.RawRetentionHours));
            int deleted = 0;

            foreach (var seg in FindSegments(DateTimeOffset.MinValue, DateTimeOffset.MaxValue))
            {
                if (seg.EndUtc > cutoff) continue;
                if (protectedPaths != null && protectedPaths.Contains(seg.Path)) continue;
                try { File.Delete(seg.Path); deleted++; }
                catch (Exception ex) { RuntimeLog.Warn("PP-PATCH", $"segment cleanup skipped: {ex.Message}"); }
            }
            return deleted;
        }

        public void Stop()
        {
            _stopping = true;
            _cts.Cancel();

            Process? p = _proc;
            if (p != null && !p.HasExited)
            {
                try
                {
                    // Ask ffmpeg to finish the current MKV segment cleanly, then wait a little.
                    p.StandardInput.WriteLine("q");
                    p.StandardInput.Flush();
                    p.WaitForExit(5000);
                }
                catch { /* stdin already closed */ }

                if (!p.HasExited)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
            }

            var tasks = new[] { _stdoutTask, _stderrTask, _dispatchTask, _watchTask }
                        .Where(t => t != null).Select(t => t!).ToArray();
            if (tasks.Length > 0)
            {
                try { Task.WaitAll(tasks, 3000); }
                catch { /* background tasks are best-effort on shutdown */ }
            }

            _frames.Writer.TryComplete();
            RuntimeLog.Info("PP-PATCH", $"capture stopped; preview frames dropped={DroppedPreviewFrames}");
        }

        public void Dispose() => Stop();
    }
}

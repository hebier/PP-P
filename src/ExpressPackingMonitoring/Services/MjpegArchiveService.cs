#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services
{
    /// <summary>
    /// PP-PATCH: offline transcode queue (yield-capable scheduling, see plan section 4.4).
    ///
    /// It is NOT on the 33 ms real-time path. While an order is being packed we never start a
    /// transcode; we wait for 3-5 min of idle, then process ONE order at a time. A scan event
    /// lets the current order finish and then pauses the queue. One order is only ~45 s of
    /// material, so no matter how slow the transcode is, it can yield within one order's time.
    ///
    /// Output stays in an MKV container at the SAME FilePath, so the DB row never changes and
    /// an in-progress playback keeps working. H.264 is for size (~35 MB vs ~189 MB), burned-in
    /// order no./time, and universal playback - NOT for real-time capture.
    /// </summary>
    public sealed class MjpegArchiveService : IDisposable
    {
        private sealed record ArchiveItem(OrderClipResult Clip, DateTimeOffset EnqueuedUtc);

        private readonly PatchOptions _opt;
        private readonly ConcurrentQueue<ArchiveItem> _queue = new ConcurrentQueue<ArchiveItem>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _worker;
        private volatile bool _recording;
        private volatile bool _warnedBacklog;
        private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;
        private long _completed;

        public int Backlog => _queue.Count;
        public long Completed => Interlocked.Read(ref _completed);

        public MjpegArchiveService(PatchOptions opt)
        {
            _opt = opt ?? throw new ArgumentNullException(nameof(opt));
        }

        public void Start()
        {
            _worker = Task.Run(WorkerLoopAsync, CancellationToken.None);
            RuntimeLog.Info("PP-PATCH", $"archive queue started (idle={_opt.ArchiveIdleMinutes} min, encoder={_opt.ArchiveEncoder})");
        }

        public void NotifyRecordingStarted()
        {
            _recording = true;
            _lastActivityUtc = DateTimeOffset.UtcNow;
        }

        public void NotifyRecordingStopped()
        {
            _recording = false;
            _lastActivityUtc = DateTimeOffset.UtcNow;
        }

        public void Enqueue(OrderClipResult clip)
        {
            _queue.Enqueue(new ArchiveItem(clip, DateTimeOffset.UtcNow));
            if (_queue.Count > _opt.ArchiveWarnBacklog && !_warnedBacklog)
            {
                _warnedBacklog = true;
                RuntimeLog.Warn("PP-PATCH", $"archive backlog is {_queue.Count} orders - disk may fill; check rolling segment retention");
            }
        }

        private async Task WorkerLoopAsync()
        {
            var idleWindow = TimeSpan.FromMinutes(Math.Max(1, _opt.ArchiveIdleMinutes));
            var maxAge = TimeSpan.FromHours(Math.Max(1, _opt.ArchiveMaxBacklogHours));

            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(5000, _cts.Token); }
                catch (OperationCanceledException) { return; }

                if (!_queue.TryPeek(out var item)) { _warnedBacklog = false; continue; }

                bool overdue = DateTimeOffset.UtcNow - item.EnqueuedUtc > maxAge;
                bool idle = DateTimeOffset.UtcNow - _lastActivityUtc >= idleWindow;

                // Recording in progress => never start (unless the 24 h safety net fires).
                // This is about isolating SSD I/O and QSV, not about a CPU percentage.
                if (_recording && !overdue) continue;
                if (!idle && !overdue) continue;

                if (!_queue.TryDequeue(out item)) continue;

                try
                {
                    await TranscodeAsync(item.Clip, _cts.Token);
                    Interlocked.Increment(ref _completed);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // A failed .partial is harmless: re-queue and retry later. Never lose the order.
                    RuntimeLog.Error("PP-PATCH", $"archive failed for {item.Clip.OrderNo}, will retry", ex);
                    _queue.Enqueue(item);
                    await Task.Delay(30000, _cts.Token);
                }

                // Re-judge idle after each order. If a scan arrived, the loop's _recording check
                // will stop us before the next one. This is the "yield within one order" guarantee.
                _lastActivityUtc = DateTimeOffset.UtcNow;
            }
        }

        private async Task TranscodeAsync(OrderClipResult clip, CancellationToken ct)
        {
            string ffmpeg = _opt.ResolveArchiveFfmpeg();
            string finalPath = clip.FilePath;                  // same path, DB row untouched
            string partialPath = finalPath + ".partial";

            string label = EscapeDrawText($"{clip.OrderNo}  {clip.StartUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
            string vf = "format=nv12,drawtext=fontfile='C\\:/Windows/Fonts/msyh.ttc'" +
                        $":text='{label}':x=24:y=24:fontsize=36:fontcolor=white:box=1:boxcolor=black@0.5";

            bool done = await TryEncodeAsync(ffmpeg, finalPath, partialPath, vf, _opt.ArchiveEncoder, ct);
            if (!done)
            {
                // The bundled 4.4.1 slim build may lack drawtext / h264_qsv.
                // Fall back to software H.264; slower queue, same structure, no real-time impact.
                RuntimeLog.Warn("PP-PATCH", $"encoder {_opt.ArchiveEncoder} unavailable for {clip.OrderNo}, falling back to {_opt.ArchiveFallbackEncoder}");
                done = await TryEncodeAsync(ffmpeg, finalPath, partialPath, vf, _opt.ArchiveFallbackEncoder, ct);
            }

            if (!done || !File.Exists(partialPath))
                throw new InvalidOperationException($"archive produced no output for {clip.OrderNo}");

            var info = new FileInfo(partialPath);
            if (info.Length < 4096)
                throw new InvalidDataException($"archive output looks truncated ({info.Length} bytes)");

            File.Move(partialPath, finalPath, overwrite: true);
            RuntimeLog.Info("PP-PATCH", $"archive ready: {clip.OrderNo} ({info.Length} bytes)");
        }

        private async Task<bool> TryEncodeAsync(
            string ffmpeg, string input, string partial, string vf, string encoder, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false
            };
            var a = psi.ArgumentList;
            a.Add("-hide_banner"); a.Add("-loglevel"); a.Add("error"); a.Add("-y");
            a.Add("-i"); a.Add(input);
            a.Add("-vf"); a.Add(vf);
            a.Add("-c:v"); a.Add(encoder);
            a.Add("-b:v"); a.Add(_opt.ArchiveBitrate);
            a.Add("-pix_fmt"); a.Add("nv12");
            a.Add("-f"); a.Add("matroska");      // extension is .partial, so the format must be explicit
            a.Add(partial);

            try
            {
                using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
                string stderr = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0)
                {
                    TryDelete(partial);
                    RuntimeLog.Warn("PP-PATCH", $"encoder {encoder} failed ({p.ExitCode}): {Truncate(stderr)}");
                    return false;
                }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                TryDelete(partial);
                RuntimeLog.Warn("PP-PATCH", $"encoder {encoder} threw: {ex.Message}");
                return false;
            }
        }

        private static string EscapeDrawText(string text) =>
            text.Replace("\\", "\\\\")
                .Replace(":", "\\:")
                .Replace("'", "\\'")
                .Replace("%", "\\%")
                .Replace("\r", " ")
                .Replace("\n", " ");

        private static string Truncate(string s, int max = 400) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s.Trim() : s.Substring(0, max).Trim());

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _worker?.Wait(3000); } catch { }
            _cts.Dispose();
        }
    }
}

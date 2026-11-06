#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services
{
    public sealed record OrderWindow(string OrderNo, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    public sealed record OrderClipResult(
        string OrderNo,
        string FilePath,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc);

    /// <summary>
    /// PP-PATCH: cuts one order out of the UTC-named rolling segments, by stream copy only.
    /// MJPEG is all-I-frame, so timestamp cutting is frame-accurate (better than H.264 GOP seeking).
    /// Takes 1-3 s => the previous order is immediately playable, with no dependency on the
    /// offline H.264 archive queue. The returned path is what the DB FilePath should point to.
    /// </summary>
    public sealed class ClipByOrderService
    {
        private readonly PatchOptions _opt;
        private readonly string _ffmpeg;

        public ClipByOrderService(PatchOptions opt)
        {
            _opt = opt ?? throw new ArgumentNullException(nameof(opt));
            _ffmpeg = AppPaths.FindFFmpeg()
                      ?? throw new InvalidOperationException("ffmpeg.exe not found (AppPaths.FindFFmpeg)");
        }

        public async Task<OrderClipResult> ClipAsync(
            OrderWindow w,
            DirectCaptureRecordingService capture,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(w.OrderNo)) throw new ArgumentException("OrderNo is empty", nameof(w));
            if (w.EndUtc <= w.StartUtc) throw new ArgumentException("Empty order window", nameof(w));

            Directory.CreateDirectory(_opt.ResolveOrderClipDir());

            var segments = capture.FindSegments(w.StartUtc, w.EndUtc);
            if (segments.Count == 0)
                throw new FileNotFoundException($"No rolling segment covers {w.StartUtc:o} .. {w.EndUtc:o} in {capture.RawDir}");

            var parts = new List<string>();
            try
            {
                foreach (var seg in segments)
                {
                    ct.ThrowIfCancellationRequested();
                    var partStart = Max(w.StartUtc, seg.StartUtc);
                    var partEnd = Min(w.EndUtc, seg.EndUtc);
                    if (partEnd <= partStart) continue;

                    double ss = (partStart - seg.StartUtc).TotalSeconds;
                    double duration = (partEnd - partStart).TotalSeconds;
                    string part = Path.Combine(Path.GetTempPath(), $"ppclip_{Guid.NewGuid():N}.mkv");
                    parts.Add(part);

                    // -ss AFTER -i on purpose: MJPEG has no keyframe dependency and this is frame accurate.
                    var args = new List<string>
                    {
                        "-hide_banner", "-loglevel", "error", "-y",
                        "-i", seg.Path,
                        "-ss", F(ss), "-t", F(duration),
                        "-map", "0:v:0", "-c:v", "copy",
                        "-avoid_negative_ts", "make_zero",
                        part
                    };
                    await RunAsync(args, ct);
                }

                if (parts.Count == 0)
                    throw new InvalidOperationException("Order window produced no clip parts");

                // Include the UTC start time: the same order number can be scanned more than
                // once (repack / return), and a bare OrderNo.mkv would silently overwrite the
                // earlier clip - unacceptable for evidence / dispute lookups.
                string stamp = w.StartUtc.UtcDateTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string finalPath = Path.Combine(_opt.ResolveOrderClipDir(),
                    SafeFileName(w.OrderNo) + "_" + stamp + ".mkv");
                string partialPath = finalPath + ".partial";
                if (File.Exists(partialPath)) File.Delete(partialPath);

                if (parts.Count == 1)
                {
                    File.Move(parts[0], partialPath);
                }
                else
                {
                    string listPath = Path.Combine(Path.GetTempPath(), $"ppconcat_{Guid.NewGuid():N}.txt");
                    try
                    {
                        var sb = new StringBuilder();
                        foreach (var p in parts)
                            sb.Append("file '").Append(p.Replace("'", "'\\''")).Append('\'').Append('\n');
                        File.WriteAllText(listPath, sb.ToString(), new UTF8Encoding(false));

                        await RunAsync(new List<string>
                        {
                            "-hide_banner", "-loglevel", "error", "-y",
                            "-f", "concat", "-safe", "0", "-i", listPath,
                            "-c", "copy", partialPath
                        }, ct);
                    }
                    finally { TryDelete(listPath); }
                }

                // Minimal integrity check: a truncated MKV would be tiny or missing its tail.
                var info = new FileInfo(partialPath);
                if (!info.Exists || info.Length < 4096)
                    throw new InvalidDataException($"Clip looks truncated ({info.Length} bytes): {partialPath}");

                // Same-volume atomic replace. The original FilePath stays valid for DB/playback.
                File.Move(partialPath, finalPath, overwrite: true);

                RuntimeLog.Info("PP-PATCH", $"clip ready: {w.OrderNo} -> {finalPath} ({info.Length} bytes)");
                return new OrderClipResult(w.OrderNo, finalPath, w.StartUtc, w.EndUtc);
            }
            finally
            {
                foreach (var p in parts) TryDelete(p);
            }
        }

        private async Task RunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(_ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)
                          ?? throw new InvalidOperationException("Failed to start ffmpeg for clip");

            string stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg clip failed ({p.ExitCode}): {stderr.Trim()}");
        }

        private static string F(double seconds) =>
            Math.Max(0, seconds).ToString("0.000", CultureInfo.InvariantCulture);

        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

        private static string SafeFileName(string orderNo)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                orderNo = orderNo.Replace(c, '_');
            return orderNo.Length > 120 ? orderNo.Substring(0, 120) : orderNo;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* temp file, best effort */ }
        }
    }
}

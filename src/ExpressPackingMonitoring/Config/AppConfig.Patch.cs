#nullable enable
using System;
using System.IO;

namespace ExpressPackingMonitoring.Config
{
    /// <summary>
    /// PP-PATCH: patch-only settings.
    /// This file is NEW and relies on `public partial class AppConfig` (Config/AppConfig.cs:140).
    /// The original AppConfig.cs is NEVER modified - this is what keeps upstream merges conflict-free.
    /// </summary>
    public partial class AppConfig
    {
        /// <summary>Direct-pipeline settings. Defaults turn the patch ON after the dll is swapped in.</summary>
        public PatchOptions Patch { get; set; } = new PatchOptions();
    }

    public sealed class PatchOptions
    {
        /// <summary>"Direct" (default) = -c copy pipeline; anything else = original rawvideo pipeline.</summary>
        public string VideoPipelineMode { get; set; } = "Direct";

        /// <summary>"Fluid" = 1280x720@30 (default), "Balanced" = 1280x720@15, "Eco" = 640x360@15.</summary>
        public string PreviewMode { get; set; } = "Fluid";

        public string CameraName { get; set; } = "YSYS X5 Camera";
        public int Width { get; set; } = 2560;
        public int Height { get; set; } = 1440;
        public int Fps { get; set; } = 30;
        public int SegmentSeconds { get; set; } = 60;

        /// <summary>60s rolling MKV segments. MUST be a local SSD, never a network drive.</summary>
        public string RawSegmentDir { get; set; } = "";

        /// <summary>Per-order MKV files (DB FilePath points here). Empty = UserDataDir\pp-orders.</summary>
        public string OrderClipDir { get; set; } = "";

        public string ArchiveEncoder { get; set; } = "h264_qsv";
        public string ArchiveFallbackEncoder { get; set; } = "libx264";
        public string ArchiveBitrate { get; set; } = "8M";
        public int ArchiveIdleMinutes { get; set; } = 5;
        public int ArchiveMaxBacklogHours { get; set; } = 24;
        public int ArchiveWarnBacklog { get; set; } = 200;

        /// <summary>Must cover pending clips + archive backlog. Rolling segments are deleted only past this window.</summary>
        public int RawRetentionHours { get; set; } = 48;

        /// <summary>Optional full ffmpeg for offline transcode (drawtext/h264_qsv). Empty = use bundled tools\ffmpeg.exe.</summary>
        public string ArchiveFfmpegPath { get; set; } = "";

        public bool IsDirect =>
            string.Equals(VideoPipelineMode, "Direct", StringComparison.OrdinalIgnoreCase);

        public string ResolveRawSegmentDir() =>
            string.IsNullOrWhiteSpace(RawSegmentDir)
                ? Path.Combine(AppPaths.UserDataDir, "pp-raw")
                : RawSegmentDir;

        public string ResolveOrderClipDir() =>
            string.IsNullOrWhiteSpace(OrderClipDir)
                ? Path.Combine(AppPaths.UserDataDir, "pp-orders")
                : OrderClipDir;

        public string ResolveArchiveFfmpeg() =>
            !string.IsNullOrWhiteSpace(ArchiveFfmpegPath) && File.Exists(ArchiveFfmpegPath)
                ? ArchiveFfmpegPath
                : (AppPaths.FindFFmpeg() ?? throw new InvalidOperationException("ffmpeg.exe not found"));

        public (int Width, int Height, int TakeEvery) GetPreviewProfile()
        {
            return PreviewMode?.ToLowerInvariant() switch
            {
                "eco" => (Width / 4, Height / 4, 2),
                "balanced" => (Width / 2, Height / 2, 2),
                _ => (Width / 2, Height / 2, 1), // fluid: 1280x720 @ 30
            };
        }
    }
}

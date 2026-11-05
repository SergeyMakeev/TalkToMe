using System.Runtime.InteropServices;

namespace TalkToMe;

internal sealed class BundledSpeech : IDisposable
{
    private const string Library = "engines/TalkToMeSpeech.dll";
    [DllImport(Library)] private static extern nint ttm_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.I1)] bool gpu);
    [DllImport(Library)] private static extern void ttm_close(nint model);
    [DllImport(Library)] private static extern nint ttm_new_job();
    [DllImport(Library)] private static extern void ttm_cancel(nint job);
    [DllImport(Library)] private static extern void ttm_free_job(nint job);
    [DllImport(Library)] private static extern void ttm_free_text(nint text);
    [DllImport(Library)] private static extern nint ttm_transcribe(nint model, nint job, float[] samples, int count,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string language, [MarshalAs(UnmanagedType.LPUTF8Str)] string glossary,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string vad, int threads);

    private readonly object sync = new();
    private nint model;
    internal static string LibraryPath => Path.Combine(AppContext.BaseDirectory, Library);

    public string Run(byte[]? pcm, string language, Settings settings, CancellationToken token)
    {
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(LibraryPath)) throw new IOException("The speech engine is missing. Extract the complete TalkToMe download and reopen the app.");
            var speech = ModelDownload.Get(settings.SpeechModel);
            var vad = ModelDownload.Get("activity");
            if (!speech.Installed(settings.ModelDirectory) || !vad.Installed(settings.ModelDirectory))
                throw new IOException("Open Settings → Models to download the speech models.");
            if (model == 0) model = ttm_open(speech.PathIn(settings.ModelDirectory), false);
            if (model == 0) throw new IOException("Whisper could not load. Check available memory or download the speech models again.");
            if (pcm == null) return "";
            var samples = new float[pcm.Length / 2];
            var shorts = MemoryMarshal.Cast<byte, short>(pcm);
            for (int i = 0; i < samples.Length; i++) samples[i] = shorts[i] / 32768f;
            if (samples.Length < 1600 || !samples.Any(s => Math.Abs(s) > 0.0001f)) return "";
            var job = ttm_new_job();
            try
            {
                using var cancel = token.Register(() => ttm_cancel(job));
                var result = ttm_transcribe(model, job, samples, samples.Length, language, string.Join(", ", settings.Glossary),
                    vad.PathIn(settings.ModelDirectory), Math.Clamp(Environment.ProcessorCount / 2, 1, 8));
                try
                {
                    token.ThrowIfCancellationRequested();
                    return Marshal.PtrToStringUTF8(result)?.Trim() ?? throw new IOException("Whisper could not finish transcription.");
                }
                finally { ttm_free_text(result); }
            }
            finally { ttm_free_job(job); }
        }
    }

    public void Dispose()
    {
        lock (sync) { if (model != 0) ttm_close(model); model = 0; }
    }
}

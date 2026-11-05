using System.Media;

namespace TalkToMe;

internal static class RecordingSounds
{
    private static readonly SoundPlayer Start = Load("start");
    private static readonly SoundPlayer Stop = Load("stop");

    private static SoundPlayer Load(string name) =>
        new(typeof(RecordingSounds).Assembly.GetManifestResourceStream($"TalkToMe.Sounds.{name}.wav")!);

    public static void Play(bool recording)
    {
        try { (recording ? Start : Stop).Play(); }
        // Playback is optional on machines without working audio output.
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException) { }
    }
}

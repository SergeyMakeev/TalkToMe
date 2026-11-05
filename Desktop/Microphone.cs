using NAudio.Wave;

namespace TalkToMe;

internal interface IAudioCapture : IDisposable
{
    Task<byte[]> Completion { get; }
    void Start();
    void Stop();
}

// WaveInEvent raises DataAvailable on its capture thread. RecordingStopped follows the final buffer.
internal sealed class Microphone : IAudioCapture
{
    private readonly WaveInEvent input;
    private readonly MemoryStream pcm = new();
    private readonly TaskCompletionSource<byte[]> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<byte[]> Completion => stopped.Task;
    public Microphone(int device)
    {
        input = new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 40 };
        input.DataAvailable += (_, e) => pcm.Write(e.Buffer, 0, e.BytesRecorded);
        input.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) stopped.TrySetException(e.Exception);
            else stopped.TrySetResult(pcm.ToArray());
        };
    }
    public void Start() => input.StartRecording();
    public void Stop() => input.StopRecording();
    public void Dispose() { input.Dispose(); pcm.Dispose(); }
}

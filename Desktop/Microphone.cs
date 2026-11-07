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
    private readonly object gate = new();
    private bool disposed;
    private readonly TaskCompletionSource<byte[]> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<byte[]> Completion => stopped.Task;
    public Microphone(int device)
    {
        input = new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 40 };
        input.DataAvailable += (_, e) => {
            try { lock (gate) { if (!disposed) pcm.Write(e.Buffer, 0, e.BytesRecorded); } }
            catch (Exception error) { stopped.TrySetException(error); }
        };
        input.RecordingStopped += (_, e) =>
        {
            lock (gate) {
                if (disposed) return;
                if (e.Exception is not null) stopped.TrySetException(e.Exception);
                else stopped.TrySetResult(pcm.ToArray());
            }
        };
    }
    public void Start() => input.StartRecording();
    public void Stop()
    {
        try { input.StopRecording(); }
        catch (Exception error) { stopped.TrySetException(error); }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        try { input.Dispose(); }
        catch (Exception error) { Diagnostics.Error("microphone.dispose", error); }
        finally { lock (gate) pcm.Dispose(); stopped.TrySetCanceled(); }
    }
}

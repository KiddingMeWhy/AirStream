
namespace AirStream.Audio;

public sealed class AudioDataEventArgs : EventArgs
{
    public byte[] Buffer { get; }
    public int BytesRecorded { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }

    public AudioDataEventArgs(
        byte[] buffer,
        int bytesRecorded,
        int sampleRate,
        int channels,
        int bitsPerSample)
    {
        Buffer = buffer;
        BytesRecorded = bytesRecorded;
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
    }
}

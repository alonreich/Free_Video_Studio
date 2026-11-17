using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FreeVideoStudio.Core.Media;

public sealed class WavAudioReader : WaveStream, ISampleProvider
{
    private readonly WaveFileReader _reader;
    private readonly SampleChannel _channel;
    private readonly int _destBytesPerSample;
    private readonly int _sourceBytesPerSample;
    private readonly long _length;
    private readonly object _lock = new();

    public string FileName { get; }

    public WavAudioReader(string path)
    {
        FileName = path;
        _reader = new WaveFileReader(path);
        var enc = _reader.WaveFormat.Encoding;
        if (enc != WaveFormatEncoding.Pcm && enc != WaveFormatEncoding.IeeeFloat && enc != WaveFormatEncoding.Extensible)
        {
            _reader.Dispose();
            throw new NotSupportedException($"'{System.IO.Path.GetFileName(path)}' is a {enc} WAV. Only PCM and IEEE-float WAV files can be played here.");
        }

        _sourceBytesPerSample = Math.Max(1, _reader.WaveFormat.BitsPerSample / 8 * _reader.WaveFormat.Channels);
        _channel = new SampleChannel(_reader, forceStereo: false);
        _destBytesPerSample = 4 * _channel.WaveFormat.Channels;
        _length = SourceToDest(_reader.Length);
    }

    public override WaveFormat WaveFormat => _channel.WaveFormat;

    public override long Length => _length;

    public override long Position
    {
        get => SourceToDest(_reader.Position);
        set { lock (_lock) { _reader.Position = DestToSource(value); } }
    }

    public float Volume
    {
        get => _channel.Volume;
        set => _channel.Volume = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Span<byte> byteSpan = buffer.AsSpan(offset, count);
        Span<float> floatSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(byteSpan);
        int samplesRead = Read(floatSpan);
        return samplesRead * 4;
    }

    public int Read(Span<float> buffer)
    {
        lock (_lock) { return _channel.Read(buffer); }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    private long SourceToDest(long sourceBytes) => _destBytesPerSample * (sourceBytes / _sourceBytesPerSample);

    private long DestToSource(long destBytes) => _sourceBytesPerSample * (destBytes / _destBytesPerSample);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _reader.Dispose();
        base.Dispose(disposing);
    }
}

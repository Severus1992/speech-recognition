using NAudio.Utils;
using NAudio.Wave;
using speach;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Whisper.net;
using Whisper.net.Ggml;

class Program
{
    private static WhisperProcessor _processor;
    private static WaveInEvent _waveSource;
    private static readonly Channel<AudioChunk> _channel = Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions());
    private const double _amplitudeThreshold = 0.02;
    private const double _energyThreshold = 500;
    private const int _silenceSoftLimit = 300;
    private const int _silenceLimit = 800;
    private static readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
    private static readonly MemoryStream _vadStream = new (1024 * 1024);
    private static bool _isRecording = false;
    private static bool _isSpeaking = false;
    private static int _silenceCounter = 0;

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Whisper Writer C# (Console) ===");
        var ggmlType = GgmlType.LargeV3Turbo;
        var modelFileName = "ggml-largev3_Q8.bin";
        if (!File.Exists(modelFileName))
        {
            await DownloadModel(modelFileName, ggmlType, QuantizationType.Q8_0);
        }

        using var factory = WhisperFactory.FromPath(modelFileName);
        _processor = factory.CreateBuilder()
            .WithLanguage("ru")
            .WithPrompt("Диктовка. Без галлюцинаций и лишних слов.")
            .Build();

        InitAudio();
        var processingTask = Task.Run(() => ProcessAudioQueue());
        var mainThreadId = Win32.GetCurrentThreadId();
        Console.CancelKeyPress += async (s, e) =>
        {
            e.Cancel = true;
            Win32.PostThreadMessageW(mainThreadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            Console.WriteLine("Завершение работы...");
        };

        Console.WriteLine("Статус: Готов к работе.");
        Console.WriteLine("Нажмите Alt + Q для записи (и еще раз для завершения).");
        Console.WriteLine("Нажмите Ctrl + C для выхода из программы.");

        try
        {
            RegisterHotKey();
            RunMessageLoop();
        }
        catch (Exception ex) 
        {
            Console.WriteLine($"Ошибка: {ex.Message}");
        }
        finally
        {
            _channel.Writer.Complete();
            await processingTask;
            Cleanup();
        }
    }

    private static void InitAudio()
    {
        _waveSource = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 1)
        };

        _waveSource.DataAvailable += (s, e) =>
        {
            if (!_isRecording)
            {
                return;
            }

            var hasSpeech = CheckForSpeech(e.Buffer.AsSpan(0, e.BytesRecorded), _amplitudeThreshold, _energyThreshold);
            if (hasSpeech)
            {
                if (!_isSpeaking)
                {
                    _isSpeaking = true;
                }

                _vadStream.Write(e.Buffer, 0, e.BytesRecorded);
                _silenceCounter = 0;
            }
            else
            {
                if (_isSpeaking)
                {
                    _silenceCounter += 100;
                    if (_silenceCounter <= _silenceSoftLimit)
                    {
                        _vadStream.Write(e.Buffer, 0, e.BytesRecorded);
                    }

                    if (_silenceCounter >= _silenceLimit)
                    {
                        _isSpeaking = false;
                        Flush();
                    }
                }
            }
        };
    }

    private static void Flush()
    {
        if (_vadStream.Length == 0)
        {
            return;
        }

        var source = _vadStream.GetBuffer();
        var length = (int)_vadStream.Position;
        byte[] buffer = _pool.Rent(length);
        Buffer.BlockCopy(source, 0, buffer, 0, length);
        _vadStream.Position = 0;
        var audioChunk = new AudioChunk(buffer, length);
        _channel.Writer.TryWrite(audioChunk);
    }

    private static async Task ProcessAudioQueue()
    {
        using var memoryStream = new MemoryStream();
        await foreach (var item in _channel.Reader.ReadAllAsync())
        {
            try
            {
                memoryStream.Position = 0;
                using (var writer = new WaveFileWriter(new IgnoreDisposeStream(memoryStream), _waveSource.WaveFormat))
                {
                    writer.Write(item.Data, 0, item.Length);
                }

                memoryStream.Position = 0;
                await foreach (var result in _processor.ProcessAsync(memoryStream))
                {
                    Win32.SendTextDirectly(result.Text);
                    Console.WriteLine(result.Text);
                }
            }
            finally
            {
                _pool.Return(item.Data);
            }
        }
    }

    private static void StartRecording()
    {
        _isRecording = true;
        _waveSource.StartRecording();
        Console.Beep(1000, 100);
        Console.WriteLine("ЗАПИСЬ ЗАПУЩЕНА...");
    }

    private static void StopRecording()
    {
        _isRecording = false;
        _waveSource.StopRecording();
        Console.Beep(400, 100);
        Console.WriteLine("ЗАПИСЬ ОСТАНОВЛЕНА...");
    }

    public static bool CheckForSpeech(ReadOnlySpan<byte> buffer, double amplitudeThreshold, double energyThreshold)
    {
        if (buffer.Length < 32)
        {
            return false;
        }

        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(buffer);

        long totalEnergy = 0;
        int samplesAboveThreshold = 0;
        int intThreshold = (int)(amplitudeThreshold * 32768);
        int negThreshold = -intThreshold;

        foreach (short sample in samples)
        {
            totalEnergy += (long)sample * sample;
            if (sample > intThreshold || sample < negThreshold)
            {
                samplesAboveThreshold++;
            }
        }

        long avgEnergy = totalEnergy / samples.Length;
        return avgEnergy > energyThreshold && samplesAboveThreshold > (samples.Length / 50);
    }

    private static async Task DownloadModel(string fileName, GgmlType ggmlType, QuantizationType quantization)
    {
        Console.WriteLine($"Downloading Model {fileName}");
        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggmlType, quantization);
        using var fileWriter = File.OpenWrite(fileName);
        await modelStream.CopyToAsync(fileWriter);
    }

    private static void RegisterHotKey()
    {
        if (Win32.RegisterHotKey(IntPtr.Zero, Win32.HOTKEY_ID, Win32.MOD_ALT, Win32.VK_Q) == 0)
        {
            throw new InvalidOperationException("Не удалось зарегистрировать Alt+Q. Проверьте, не занята ли комбинация.");
        }
    }

    private static void RunMessageLoop()
    {
        while (Win32.GetMessage(out Win32.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Win32.WM_HOTKEY)
            {
                if (!_isRecording)
                {
                    StartRecording();
                }
                else
                {
                    StopRecording();
                }
            }
        }
    }

    static void Cleanup()
    {
        Win32.UnregisterHotKey(IntPtr.Zero, Win32.HOTKEY_ID);
        _waveSource?.Dispose();
        _processor?.Dispose();
        Console.WriteLine("Приложение завершено");
    }

    public readonly record struct AudioChunk(byte[] Data, int Length);
}
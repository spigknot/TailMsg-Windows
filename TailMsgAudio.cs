using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TailMsg
{
    // Captura PCM 16 kHz mono 16 bits via winmm (waveIn). Mantém buffers
    // girando durante a gravação e devolve um WAV completo no Stop.
    internal sealed class WaveRecorder : IDisposable
    {
        private const int WaveMapper = -1;
        private const int WaveFormatPcm = 1;
        private const int CallbackNull = 0;
        private const int MmSysErrNoError = 0;
        private const int WhdrDone = 0x00000001;
        private const int BufferSeconds = 2;
        private const int BufferCount = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        private static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(
            out IntPtr handle,
            int deviceId,
            ref WaveFormatEx format,
            IntPtr callback,
            IntPtr instance,
            int flags);

        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr handle, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr handle);

        [DllImport("winmm.dll")]
        private static extern int waveInStop(IntPtr handle);

        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr handle);

        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr handle);

        [DllImport("winmm.dll")]
        private static extern int waveInGetErrorText(int code, StringBuilder text, int size);

        private readonly object sync = new object();
        private readonly List<IntPtr> headerPointers = new List<IntPtr>();
        private readonly List<IntPtr> dataPointers = new List<IntPtr>();
        private readonly List<byte[]> blocks = new List<byte[]>();
        private IntPtr handle = IntPtr.Zero;
        private System.Threading.Timer recycleTimer;
        private bool recording;
        private bool paused;
        private DateTime startedUtc;
        private double pausedSeconds;
        private DateTime pausedAtUtc;
        private int recordedBytes;
        private bool limitReached;
        private int requeuedBuffers;
        private int requeueFailures;
        private int lastRequeueError;
        private uint lastRequeueFlags;
        private int lastPrepareError;
        private int queueDepth;

        public bool IsRecording
        {
            get { lock (sync) { return recording && !paused; } }
        }

        public bool IsPaused
        {
            get { lock (sync) { return recording && paused; } }
        }

        public bool IsActive
        {
            get { lock (sync) { return recording; } }
        }

        public bool LimitReached
        {
            get { lock (sync) { return limitReached; } }
        }

        public double ElapsedSeconds
        {
            get
            {
                lock (sync)
                {
                    double captured = recordedBytes / (double)BytesPerSecond;
                    if (!recording) return captured;
                    double wall = (DateTime.UtcNow - startedUtc).TotalSeconds -
                        pausedSeconds;
                    if (paused) wall -= (DateTime.UtcNow - pausedAtUtc).TotalSeconds;
                    if (wall < 0) wall = 0;
                    return Math.Max(captured, wall);
                }
            }
        }

        private static int BytesPerSecond
        {
            get
            {
                return TailMsgProtocol.AudioSampleRate * TailMsgProtocol.AudioChannels *
                    (TailMsgProtocol.AudioBitsPerSample / 8);
            }
        }

        public static int AvailableDevices
        {
            get { return (int)waveInGetNumDevs(); }
        }

        // Abre o microfone e começa a gravar. Devolve false com mensagem clara
        // quando não há dispositivo ou o Windows recusa a abertura.
        public bool Start(out string error)
        {
            error = "";
            lock (sync)
            {
                if (recording) return true;
                if (waveInGetNumDevs() == 0)
                {
                    error = "Nenhum microfone foi encontrado neste computador.";
                    return false;
                }

                WaveFormatEx format = new WaveFormatEx();
                format.wFormatTag = WaveFormatPcm;
                format.nChannels = (ushort)TailMsgProtocol.AudioChannels;
                format.nSamplesPerSec = (uint)TailMsgProtocol.AudioSampleRate;
                format.wBitsPerSample = (ushort)TailMsgProtocol.AudioBitsPerSample;
                format.nBlockAlign = (ushort)(format.nChannels * format.wBitsPerSample / 8);
                format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;

                int result = waveInOpen(
                    out handle,
                    WaveMapper,
                    ref format,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CallbackNull);
                if (result != MmSysErrNoError)
                {
                    handle = IntPtr.Zero;
                    error = "Não foi possível abrir o microfone: " + ErrorText(result);
                    return false;
                }

                int bufferBytes = BytesPerSecond * BufferSeconds;
                for (int index = 0; index < BufferCount; index++)
                {
                    IntPtr headerPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveHdr)));
                    IntPtr dataPointer = Marshal.AllocHGlobal(bufferBytes);
                    WaveHdr header = new WaveHdr();
                    header.lpData = dataPointer;
                    header.dwBufferLength = (uint)bufferBytes;
                    Marshal.StructureToPtr(header, headerPointer, false);
                    headerPointers.Add(headerPointer);
                    dataPointers.Add(dataPointer);

                    if (waveInPrepareHeader(handle, headerPointer, Marshal.SizeOf(typeof(WaveHdr))) != MmSysErrNoError ||
                        waveInAddBuffer(handle, headerPointer, Marshal.SizeOf(typeof(WaveHdr))) != MmSysErrNoError)
                    {
                        ReleaseDevice();
                        error = "Não foi possível preparar os buffers de gravação.";
                        return false;
                    }
                }

                blocks.Clear();
                recordedBytes = 0;
                limitReached = false;
                requeuedBuffers = 0;
                requeueFailures = 0;
                lastRequeueError = 0;
                startedUtc = DateTime.UtcNow;
                recording = true;

                if (waveInStart(handle) != MmSysErrNoError)
                {
                    recording = false;
                    ReleaseDevice();
                    error = "Não foi possível iniciar a gravação.";
                    return false;
                }

                recycleTimer = new System.Threading.Timer(Recycle, null, 100, 100);
                return true;
            }
        }

        // Encerra a gravação e devolve o WAV completo (nulo se nada foi
        // capturado).
        // Pausa a captura mantendo o dispositivo aberto: os buffers continuam
        // na fila e voltam a receber áudio no Resume.
        public bool Pause(out string error)
        {
            error = "";
            lock (sync)
            {
                if (!recording || paused) return true;
                if (waveInStop(handle) != MmSysErrNoError)
                {
                    error = "Não foi possível pausar a gravação.";
                    return false;
                }
                CollectFinishedBuffers();
                RequeueBuffers();
                paused = true;
                pausedAtUtc = DateTime.UtcNow;
                return true;
            }
        }

        public bool Resume(out string error)
        {
            error = "";
            lock (sync)
            {
                if (!recording || !paused) return true;
                pausedSeconds += (DateTime.UtcNow - pausedAtUtc).TotalSeconds;
                pausedAtUtc = DateTime.UtcNow;
                paused = false;
                if (waveInStart(handle) != MmSysErrNoError)
                {
                    paused = true;
                    error = "Não foi possível retomar a gravação.";
                    return false;
                }
                return true;
            }
        }

        private void RequeueBuffers()
        {
            foreach (IntPtr headerPointer in headerPointers)
            {
                WaveHdr header = (WaveHdr)Marshal.PtrToStructure(
                    headerPointer,
                    typeof(WaveHdr));
                header.dwBytesRecorded = 0;
                header.dwFlags &= ~((uint)WhdrDone);
                Marshal.StructureToPtr(header, headerPointer, false);
                waveInAddBuffer(handle, headerPointer, Marshal.SizeOf(typeof(WaveHdr)));
            }
        }

        // Cópia do que já foi capturado a partir de um deslocamento, para a
        // transcrição incremental. Não para a gravação nem consome blocos.
        public byte[] SnapshotFrom(int offset)
        {
            lock (sync)
            {
                if (offset < 0) offset = 0;
                if (recordedBytes <= offset) return new byte[0];
                byte[] result = new byte[recordedBytes - offset];
                int position = 0;
                int skip = offset;
                foreach (byte[] block in blocks)
                {
                    if (skip >= block.Length)
                    {
                        skip -= block.Length;
                        continue;
                    }
                    int copy = Math.Min(block.Length - skip, result.Length - position);
                    if (copy <= 0) break;
                    Buffer.BlockCopy(block, skip, result, position, copy);
                    position += copy;
                    skip = 0;
                }
                if (position == result.Length) return result;
                byte[] trimmed = new byte[position];
                Buffer.BlockCopy(result, 0, trimmed, 0, position);
                return trimmed;
            }
        }

        public int CapturedBytes
        {
            get { lock (sync) { return recordedBytes; } }
        }

        // Diagnóstico da reciclagem dos buffers de captura: se os buffers não
        // voltam para a fila, o áudio para de acumular depois de alguns
        // segundos.
        public int RequeuedBuffers
        {
            get { lock (sync) { return requeuedBuffers; } }
        }

        public int RequeueFailures
        {
            get { lock (sync) { return requeueFailures; } }
        }

        public int LastRequeueError
        {
            get { lock (sync) { return lastRequeueError; } }
        }

        public uint LastRequeueFlags
        {
            get { lock (sync) { return lastRequeueFlags; } }
        }

        public int LastPrepareError
        {
            get { lock (sync) { return lastPrepareError; } }
        }

        public int QueueDepth
        {
            get { lock (sync) { return queueDepth; } }
        }

        // Empacota amostras PCM cruas no mesmo contêiner WAV da gravação.
        public static byte[] WrapPcm(byte[] samples)
        {
            if (samples == null || samples.Length == 0) return null;
            return BuildWav(samples, samples.Length);
        }

        public AudioPayload Stop()
        {
            lock (sync)
            {
                if (!recording || handle == IntPtr.Zero)
                {
                    return null;
                }

                StopTimer();
                waveInStop(handle);
                waveInReset(handle);
                recording = false;
                paused = false;

                // O reset marca os buffers pendentes de forma assíncrona no
                // driver: esperar aqui evita descartar a gravação inteira.
                DateTime deadline = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < deadline)
                {
                    CollectFinishedBuffers();
                    if (AllBuffersFinished()) break;
                    Thread.Sleep(20);
                }
                CollectFinishedBuffers();

                byte[] samples = new byte[recordedBytes];
                int offset = 0;
                foreach (byte[] block in blocks)
                {
                    int copy = Math.Min(block.Length, samples.Length - offset);
                    if (copy <= 0) break;
                    Buffer.BlockCopy(block, 0, samples, offset, copy);
                    offset += copy;
                }
                blocks.Clear();

                ReleaseDevice();

                if (offset < BytesPerSecond / 10)
                {
                    return null;   // menos de 100 ms: não é áudio útil
                }

                AudioPayload payload = new AudioPayload();
                payload.WavBytes = BuildWav(samples, offset);
                payload.DurationMilliseconds = (int)((offset * 1000L) / BytesPerSecond);
                return payload;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                StopTimer();
                if (recording && handle != IntPtr.Zero)
                {
                    waveInStop(handle);
                    waveInReset(handle);
                    recording = false;
                }
                ReleaseDevice();
                blocks.Clear();
            }
        }

        private void StopTimer()
        {
            if (recycleTimer == null) return;
            recycleTimer.Dispose();
            recycleTimer = null;
        }

        private void Recycle(object state)
        {
            lock (sync)
            {
                if (!recording) return;
                CollectFinishedBuffers();

                if (limitReached) return;
                if (ElapsedSeconds >= TailMsgProtocol.MaximumAudioSeconds)
                {
                    // Teto de segurança: a interface encerra o anexo ao ver a
                    // marca, evitando gravação sem fim na memória.
                    limitReached = true;
                }
            }
        }

        private bool AllBuffersFinished()
        {
            if (headerPointers.Count == 0) return true;
            foreach (IntPtr headerPointer in headerPointers)
            {
                WaveHdr header = (WaveHdr)Marshal.PtrToStructure(
                    headerPointer,
                    typeof(WaveHdr));
                if ((header.dwFlags & WhdrDone) == 0) return false;
            }
            return true;
        }

        // Copia o conteúdo dos buffers prontos e devolve os consumidos ao
        // gravador. O buffer é marcado como consumido mesmo fora da gravação,
        // para a espera do Stop não duplicar o mesmo bloco.
        private void CollectFinishedBuffers()
        {
            foreach (IntPtr headerPointer in headerPointers)
            {
                WaveHdr header = (WaveHdr)Marshal.PtrToStructure(headerPointer, typeof(WaveHdr));
                if ((header.dwFlags & WhdrDone) == 0)
                {
                    continue;
                }

                int length = (int)header.dwBytesRecorded;
                if (length > 0 && header.lpData != IntPtr.Zero)
                {
                    byte[] block = new byte[length];
                    Marshal.Copy(header.lpData, block, 0, length);
                    blocks.Add(block);
                    recordedBytes += length;
                }

                header.dwBytesRecorded = 0;
                // Limpa somente WHDR_DONE: zerar dwFlags apagaria o bit
                // WHDR_PREPARED marcado pelo driver e o buffer não poderia
                // voltar para a fila (WAVERR_UNPREPARED = 34).
                header.dwFlags &= ~((uint)WhdrDone);
                Marshal.StructureToPtr(header, headerPointer, false);

                if (recording)
                {
                    int added = waveInAddBuffer(
                        handle,
                        headerPointer,
                        Marshal.SizeOf(typeof(WaveHdr)));
                    if (added == MmSysErrNoError)
                    {
                        requeuedBuffers++;
                        queueDepth++;
                    }
                    else
                    {
                        lastRequeueFlags = header.dwFlags;
                        // Reserva: re-prepara o cabeçalho e tenta de novo.
                        waveInUnprepareHeader(handle, headerPointer, Marshal.SizeOf(typeof(WaveHdr)));
                        int prepared = waveInPrepareHeader(handle, headerPointer, Marshal.SizeOf(typeof(WaveHdr)));
                        lastPrepareError = prepared;
                        if (prepared == MmSysErrNoError)
                        {
                            added = waveInAddBuffer(handle, headerPointer, Marshal.SizeOf(typeof(WaveHdr)));
                        }
                        if (added == MmSysErrNoError)
                        {
                            requeuedBuffers++;
                            queueDepth++;
                        }
                        else
                        {
                            requeueFailures++;
                            lastRequeueError = added;
                        }
                    }
                }
            }
        }

        private void ReleaseDevice()
        {
            if (handle == IntPtr.Zero)
            {
                FreeBuffers();
                return;
            }

            for (int index = 0; index < headerPointers.Count; index++)
            {
                waveInUnprepareHeader(
                    handle,
                    headerPointers[index],
                    Marshal.SizeOf(typeof(WaveHdr)));
            }
            waveInClose(handle);
            handle = IntPtr.Zero;
            FreeBuffers();
        }

        private void FreeBuffers()
        {
            foreach (IntPtr pointer in headerPointers)
            {
                Marshal.FreeHGlobal(pointer);
            }
            foreach (IntPtr pointer in dataPointers)
            {
                Marshal.FreeHGlobal(pointer);
            }
            headerPointers.Clear();
            dataPointers.Clear();
        }

        private static byte[] BuildWav(byte[] samples, int length)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int bytesPerSecond = BytesPerSecond;
                short blockAlign = (short)(TailMsgProtocol.AudioChannels *
                    (TailMsgProtocol.AudioBitsPerSample / 8));
                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + length);
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)WaveFormatPcm);
                writer.Write((short)TailMsgProtocol.AudioChannels);
                writer.Write(TailMsgProtocol.AudioSampleRate);
                writer.Write(bytesPerSecond);
                writer.Write(blockAlign);
                writer.Write((short)TailMsgProtocol.AudioBitsPerSample);
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(length);
                writer.Write(samples, 0, length);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static string ErrorText(int code)
        {
            StringBuilder text = new StringBuilder(256);
            waveInGetErrorText(code, text, text.Capacity);
            return code + " (" + text + ")";
        }
    }

    // Reprodução PCM com play, pause, retomada e posição — usada na timeline.
    internal sealed class WavePlayer : IDisposable
    {
        private const int WaveMapper = -1;
        private const int WaveFormatPcm = 1;
        private const int CallbackNull = 0;
        private const int MmSysErrNoError = 0;
        private const int WhdrDone = 0x00000001;
        private const double SliceSeconds = 0.12;
        private const int PendingLimit = 16;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(
            out IntPtr handle,
            int deviceId,
            ref WaveFormatEx format,
            IntPtr callback,
            IntPtr instance,
            int flags);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveOutPause(IntPtr handle);

        [DllImport("winmm.dll")]
        private static extern int waveOutRestart(IntPtr handle);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr handle);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr handle);

        private readonly object sync = new object();
        private byte[] samples;
        private int sampleRate;
        private int blockAlign;
        private int sliceBytes;
        private int nextOffset;
        private int playedSamples;
        private bool paused;
        private bool playing;
        private IntPtr handle = IntPtr.Zero;
        private readonly List<IntPtr> slices = new List<IntPtr>();
        private System.Threading.Timer pumpTimer;

        public double DurationSeconds { get; private set; }

        public bool IsPlaying
        {
            get { lock (sync) { return playing && !paused; } }
        }

        public bool IsPaused
        {
            get { lock (sync) { return playing && paused; } }
        }

        public double PositionSeconds
        {
            get
            {
                lock (sync)
                {
                    // A posição vem dos buffers já reproduzidos. O
                    // waveOutGetPosition conta amostras na taxa do dispositivo
                    // (com conversão para 48 kHz, por exemplo), o que marcaria
                    // o fim do áudio cedo demais.
                    double result = playedSamples / (double)Math.Max(1, sampleRate);
                    if (result > DurationSeconds) result = DurationSeconds;
                    return result;
                }
            }
        }

        public bool Load(byte[] wavBytes, out string error)
        {
            error = "";
            int dataOffset;
            int dataBytes;
            short channels;
            short bits;
            int rate;
            if (!TryReadWaveData(wavBytes, out dataOffset, out dataBytes, out rate, out channels, out bits))
            {
                error = "O arquivo de áudio não está no formato esperado.";
                return false;
            }

            samples = new byte[dataBytes];
            Buffer.BlockCopy(wavBytes, dataOffset, samples, 0, dataBytes);
            sampleRate = rate;
            blockAlign = (channels * bits) / 8;
            if (blockAlign <= 0)
            {
                error = "O arquivo de áudio não está no formato esperado.";
                return false;
            }
            sliceBytes = Math.Max(blockAlign, (int)(rate * (bits / 8.0) * channels * SliceSeconds));
            DurationSeconds = dataBytes / (double)(rate * blockAlign);
            nextOffset = 0;
            playedSamples = 0;
            return true;
        }

        public bool Play(out string error)
        {
            error = "";
            lock (sync)
            {
                if (samples == null) { error = "Nada para tocar."; return false; }
                if (playing && paused)
                {
                    return Resume(out error);
                }
                if (playing) return true;

                WaveFormatEx format = new WaveFormatEx();
                format.wFormatTag = WaveFormatPcm;
                format.nChannels = 1;
                format.nSamplesPerSec = (uint)sampleRate;
                format.wBitsPerSample = 16;
                format.nBlockAlign = (ushort)blockAlign;
                format.nAvgBytesPerSec = (uint)(sampleRate * blockAlign);

                int result = waveOutOpen(
                    out handle,
                    WaveMapper,
                    ref format,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CallbackNull);
                if (result != MmSysErrNoError)
                {
                    handle = IntPtr.Zero;
                    error = "Não foi possível abrir a saída de áudio.";
                    return false;
                }

                playedSamples = 0;
                if (nextOffset >= samples.Length)
                {
                    nextOffset = 0;
                }
                playedSamples = nextOffset / blockAlign;
                paused = false;
                playing = true;
                pumpTimer = new System.Threading.Timer(Pump, null, 0, 80);
                return true;
            }
        }

        public bool Pause(out string error)
        {
            error = "";
            lock (sync)
            {
                if (!playing || paused) return true;
                if (waveOutPause(handle) != MmSysErrNoError)
                {
                    error = "Não foi possível pausar o áudio.";
                    return false;
                }
                CollectSamples();
                paused = true;
                return true;
            }
        }

        public bool Resume(out string error)
        {
            error = "";
            lock (sync)
            {
                if (!playing || !paused) return true;
                if (waveOutRestart(handle) != MmSysErrNoError)
                {
                    error = "Não foi possível retomar o áudio.";
                    return false;
                }
                paused = false;
                return true;
            }
        }

        public void Stop()
        {
            lock (sync)
            {
                StopTimer();
                if (handle != IntPtr.Zero)
                {
                    waveOutReset(handle);
                }
                playing = false;
                paused = false;
                // Parar recoloca a timeline no início.
                nextOffset = 0;
                playedSamples = 0;
                ReleaseSlices();
                if (handle != IntPtr.Zero)
                {
                    waveOutClose(handle);
                    handle = IntPtr.Zero;
                }
            }
        }

        // Reposiciona a reprodução; se estiver tocando, continua do ponto.
        public void Seek(double seconds)
        {
            lock (sync)
            {
                if (samples == null) return;
                double clamped = Math.Max(0, Math.Min(seconds, DurationSeconds));
                int offset = (int)(clamped * sampleRate) * blockAlign;
                offset -= offset % Math.Max(1, blockAlign);
                bool resume = playing && !paused;
                bool wasPlaying = playing;

                StopTimer();
                if (handle != IntPtr.Zero)
                {
                    waveOutReset(handle);
                    ReleaseSlices();
                    waveOutClose(handle);
                    handle = IntPtr.Zero;
                }
                playing = false;
                paused = false;
                nextOffset = offset;
                playedSamples = offset / Math.Max(1, blockAlign);

                if (wasPlaying || resume)
                {
                    string error;
                    Play(out error);
                }
            }
        }

        // Descarta o trecho já tocado e renova os buffers pendentes.
        public void Restart()
        {
            lock (sync)
            {
                nextOffset = 0;
                playedSamples = 0;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                StopTimer();
                if (handle != IntPtr.Zero)
                {
                    waveOutReset(handle);
                    ReleaseSlices();
                    waveOutClose(handle);
                    handle = IntPtr.Zero;
                }
                playing = false;
                paused = false;
                samples = null;
                playedSamples = 0;
                nextOffset = 0;
            }
        }

        private void StopTimer()
        {
            if (pumpTimer == null) return;
            pumpTimer.Dispose();
            pumpTimer = null;
        }

        private void Pump(object state)
        {
            lock (sync)
            {
                if (!playing || paused || handle == IntPtr.Zero) return;
                CollectSamples();

                int pending = 0;
                foreach (IntPtr pointer in slices)
                {
                    WaveHdr header = (WaveHdr)Marshal.PtrToStructure(pointer, typeof(WaveHdr));
                    if ((header.dwFlags & WhdrDone) == 0) pending++;
                }

                while (pending < PendingLimit && nextOffset < samples.Length)
                {
                    int count = Math.Min(sliceBytes, samples.Length - nextOffset);
                    IntPtr headerPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveHdr)));
                    IntPtr dataPointer = Marshal.AllocHGlobal(count);
                    Marshal.Copy(samples, nextOffset, dataPointer, count);

                    WaveHdr header = new WaveHdr();
                    header.lpData = dataPointer;
                    header.dwBufferLength = (uint)count;
                    Marshal.StructureToPtr(header, headerPointer, false);
                    waveOutPrepareHeader(handle, headerPointer, Marshal.SizeOf(typeof(WaveHdr)));
                    waveOutWrite(handle, headerPointer, Marshal.SizeOf(typeof(WaveHdr)));
                    slices.Add(headerPointer);
                    nextOffset += count;
                    pending++;
                }

                if (nextOffset >= samples.Length && pending == 0)
                {
                    // Fim do áudio: fecha a sessão e deixa a posição no início
                    // para o próximo toque.
                    StopTimer();
                    waveOutClose(handle);
                    handle = IntPtr.Zero;
                    playing = false;
                    paused = false;
                    nextOffset = 0;
                    playedSamples = 0;
                }
            }
        }

        private void CollectSamples()
        {
            for (int index = slices.Count - 1; index >= 0; index--)
            {
                IntPtr headerPointer = slices[index];
                WaveHdr header = (WaveHdr)Marshal.PtrToStructure(headerPointer, typeof(WaveHdr));
                if ((header.dwFlags & WhdrDone) == 0)
                {
                    continue;
                }
                // O buffer só fica pronto depois de tocar: é isso que move a
                // posição da timeline.
                playedSamples += (int)header.dwBufferLength / Math.Max(1, blockAlign);
                waveOutUnprepareHeader(handle, headerPointer, Marshal.SizeOf(typeof(WaveHdr)));
                if (header.lpData != IntPtr.Zero) Marshal.FreeHGlobal(header.lpData);
                Marshal.FreeHGlobal(headerPointer);
                slices.RemoveAt(index);
            }
        }

        private void ReleaseSlices()
        {
            foreach (IntPtr headerPointer in slices)
            {
                WaveHdr header = (WaveHdr)Marshal.PtrToStructure(headerPointer, typeof(WaveHdr));
                if (handle != IntPtr.Zero)
                {
                    waveOutUnprepareHeader(handle, headerPointer, Marshal.SizeOf(typeof(WaveHdr)));
                }
                if (header.lpData != IntPtr.Zero) Marshal.FreeHGlobal(header.lpData);
                Marshal.FreeHGlobal(headerPointer);
            }
            slices.Clear();
        }

        private static bool TryReadWaveData(
            byte[] wavBytes,
            out int dataOffset,
            out int dataBytes,
            out int sampleRate,
            out short channels,
            out short bitsPerSample)
        {
            dataOffset = 0;
            dataBytes = 0;
            sampleRate = 0;
            channels = 0;
            bitsPerSample = 0;
            if (!TailMsgProtocol.TryReadWavFormat(
                wavBytes,
                out dataBytes,
                out sampleRate,
                out channels,
                out bitsPerSample))
            {
                return false;
            }

            int offset = 12;
            while (offset + 8 <= wavBytes.Length)
            {
                int size = wavBytes[offset + 4] |
                    (wavBytes[offset + 5] << 8) |
                    (wavBytes[offset + 6] << 16) |
                    (wavBytes[offset + 7] << 24);
                string chunkId = "" + (char)wavBytes[offset] + (char)wavBytes[offset + 1] +
                    (char)wavBytes[offset + 2] + (char)wavBytes[offset + 3];
                if (chunkId == "data")
                {
                    dataOffset = offset + 8;
                    if (dataOffset + dataBytes > wavBytes.Length)
                    {
                        dataBytes = wavBytes.Length - dataOffset;
                    }
                    return dataBytes > 0;
                }
                offset += 8 + size + (size % 2);
            }
            return false;
        }
    }

    internal enum IconGlyph
    {
        // None mantém o espaço do botão sem desenhar nada: o slot fica fixo
        // (como as colunas de botões do SIG) e os vizinhos não se deslocam.
        None = 0,
        Microphone = 1,
        Check = 2,
        Play = 3,
        Pause = 4,
        Spinner = 5
    }

    // Botão redondo com os mesmos ícones do SIG Windows (coordenadas e cores
    // copiadas do canvas 44x44 dele). Desenhado em GDI+ com antialiasing, sem
    // depender de fontes de símbolos nem de imagens externas.
    internal sealed class IconButton : Control
    {
        private const float SigCanvas = 44F;

        public IconGlyph Glyph = IconGlyph.Microphone;
        // Quando definido, o botão desenha esta imagem (PNG embutido, 256x256
        // com transparência) em vez do desenho vetorial. O círculo de fundo já
        // faz parte da imagem.
        public Image SourceImage;
        // Selo de contagem ("x2", "x3") no canto inferior direito, usado pelo
        // clipe quando há vários arquivos (pendentes ou no histórico).
        private string badgeText = "";
        public string BadgeText
        {
            get { return badgeText; }
            set
            {
                string text = value ?? "";
                if (badgeText != text)
                {
                    badgeText = text;
                    Invalidate();
                }
            }
        }
        // Círculo de fundo.
        public Color CircleColor = Color.White;
        public Color CircleOutline = Color.FromArgb(118, 130, 130);
        // Corpo do microfone (arcos, haste e base) e cápsula.
        public Color BodyColor = Color.FromArgb(83, 101, 101);
        public Color CapsuleColor = Color.FromArgb(83, 101, 101);
        public Color CapsuleOutline = Color.Empty;
        public bool DrawMicrophoneBase;
        // Check verde (gravação) e controles da pausa (barras ou play).
        public Color CheckColor = Color.FromArgb(61, 220, 102);
        public Color ControlColor = Color.FromArgb(27, 91, 146);
        public Color ControlOutline = Color.FromArgb(22, 70, 111);

        private bool hovered;
        private bool pressed;
        private Bitmap scaledImage;
        private int scaledSide;

        public IconButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor,
                true);
            Cursor = Cursors.Hand;
            TabStop = false;
            BackColor = Color.Transparent;
            // Expõe o controle como botão para a acessibilidade/automação:
            // leitores de tela e a UIA passam a oferecer a ação de pressionar.
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleDefaultActionDescription = "Pressionar";
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Ícone em imagem: escalado com alta qualidade e mantido em cache
            // por tamanho (a origem tem 256x256 e o botão tem 44 ou 22 px).
            if (SourceImage != null)
            {
                int sidePixels = Math.Max(8, Math.Min(Width, Height));
                if (scaledImage == null || scaledSide != sidePixels)
                {
                    if (scaledImage != null) scaledImage.Dispose();
                    scaledImage = new Bitmap(sidePixels, sidePixels);
                    using (Graphics target = Graphics.FromImage(scaledImage))
                    {
                        target.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        target.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        target.SmoothingMode = SmoothingMode.AntiAlias;
                        target.Clear(Color.Transparent);
                        target.DrawImage(SourceImage, 0, 0, sidePixels, sidePixels);
                    }
                    scaledSide = sidePixels;
                }

                float alpha = 1F;
                if (!Enabled) alpha = 0.45F;
                else if (pressed) alpha = 0.86F;
                else if (hovered) alpha = 0.94F;

                int imageLeft = (Width - sidePixels) / 2;
                int imageTop = (Height - sidePixels) / 2;
                if (alpha >= 0.999F)
                {
                    graphics.DrawImageUnscaled(scaledImage, imageLeft, imageTop);
                }
                else
                {
                    System.Drawing.Imaging.ColorMatrix matrix =
                        new System.Drawing.Imaging.ColorMatrix();
                    matrix.Matrix33 = alpha;
                    using (System.Drawing.Imaging.ImageAttributes attributes =
                        new System.Drawing.Imaging.ImageAttributes())
                    {
                        attributes.SetColorMatrix(matrix);
                        graphics.DrawImage(
                            scaledImage,
                            new Rectangle(imageLeft, imageTop, sidePixels, sidePixels),
                            0,
                            0,
                            sidePixels,
                            sidePixels,
                            GraphicsUnit.Pixel,
                            attributes);
                    }
                }
                DrawBadge(graphics);
                return;
            }

            Color circle = CircleColor;
            if (!Enabled)
            {
                circle = IconButton.Blend(CircleColor, Color.White, 0.45F);
            }
            else if (pressed)
            {
                circle = IconButton.Blend(CircleColor, Color.Black, 0.10F);
            }
            else if (hovered)
            {
                circle = IconButton.Blend(CircleColor, Color.White, 0.16F);
            }

            if (Glyph == IconGlyph.None) return;

            float side = Math.Min(Width, Height);
            float scale = side / SigCanvas;
            float offsetX = (Width - side) / 2F;
            float offsetY = (Height - side) / 2F;

            using (SolidBrush brush = new SolidBrush(circle))
            using (Pen outline = new Pen(CircleOutline, Math.Max(1F, 2F * scale)))
            {
                graphics.FillEllipse(
                    brush,
                    offsetX + (4F * scale),
                    offsetY + (4F * scale),
                    36F * scale,
                    36F * scale);
                graphics.DrawEllipse(
                    outline,
                    offsetX + (4F * scale),
                    offsetY + (4F * scale),
                    36F * scale,
                    36F * scale);
            }

            DrawGlyph(
                graphics,
                Glyph,
                offsetX,
                offsetY,
                scale,
                BodyColor,
                CapsuleColor,
                CapsuleOutline,
                DrawMicrophoneBase,
                CheckColor,
                ControlColor,
                ControlOutline);
            DrawBadge(graphics);
        }

        // Selo "xN" no canto inferior direito, sobre o ícone (clipe com
        // vários arquivos). Cabe em botões de 22 px ou mais.
        private void DrawBadge(Graphics graphics)
        {
            if (String.IsNullOrEmpty(badgeText)) return;
            float side = Math.Min(Width, Height);
            float fontSize = side < 30F ? 7F : 8F;
            using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold))
            {
                SizeF textSize = graphics.MeasureString(badgeText, font);
                int pillWidth = (int)Math.Ceiling(textSize.Width) + 8;
                int pillHeight = (int)Math.Ceiling(textSize.Height) + 2;
                int x = Math.Max(0, Width - pillWidth - 1);
                int y = Math.Max(0, Height - pillHeight - 1);
                using (SolidBrush background = new SolidBrush(Color.FromArgb(31, 41, 55)))
                using (SolidBrush foreground = new SolidBrush(Color.White))
                {
                    graphics.FillRectangle(background, x, y, pillWidth, pillHeight);
                    graphics.DrawString(badgeText, font, foreground, x + 4, y + 1);
                }
            }
        }

        // Coordenadas e cores copiadas do SIG Windows (canvas 44 x 44).
        internal static void DrawGlyph(
            Graphics graphics,
            IconGlyph glyph,
            float offsetX,
            float offsetY,
            float scale,
            Color bodyColor,
            Color capsuleColor,
            Color capsuleOutline,
            bool drawBase,
            Color checkColor,
            Color controlColor,
            Color controlOutline)
        {
            switch (glyph)
            {
                case IconGlyph.Microphone:
                {
                    using (SolidBrush capsule = new SolidBrush(capsuleColor))
                    using (Pen pen = new Pen(bodyColor, Scaled(3F, scale)))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        if (capsuleOutline != Color.Empty)
                        {
                            using (Pen capsulePen = new Pen(capsuleOutline, Scaled(2F, scale)))
                            {
                                graphics.FillEllipse(capsule, X(17F, offsetX, scale), Y(10F, offsetY, scale), Scaled(10F, scale), Scaled(16F, scale));
                                graphics.DrawEllipse(capsulePen, X(17F, offsetX, scale), Y(10F, offsetY, scale), Scaled(10F, scale), Scaled(16F, scale));
                            }
                        }
                        else
                        {
                            graphics.FillEllipse(capsule, X(17F, offsetX, scale), Y(10F, offsetY, scale), Scaled(10F, scale), Scaled(16F, scale));
                        }

                        graphics.DrawLine(pen, X(22F, offsetX, scale), Y(26F, offsetY, scale), X(22F, offsetX, scale), Y(33F, offsetY, scale));
                        graphics.DrawArc(pen, X(13F, offsetX, scale), Y(18F, offsetY, scale), Scaled(18F, scale), Scaled(16F, scale), 200F, 140F);
                        if (drawBase)
                        {
                            graphics.DrawLine(pen, X(16F, offsetX, scale), Y(34F, offsetY, scale), X(28F, offsetX, scale), Y(34F, offsetY, scale));
                        }
                    }
                    break;
                }

                case IconGlyph.Check:
                {
                    using (Pen pen = new Pen(checkColor, Scaled(5F, scale)))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        graphics.DrawLine(pen, X(15F, offsetX, scale), Y(21F, offsetY, scale), X(20F, offsetX, scale), Y(27F, offsetY, scale));
                        graphics.DrawLine(pen, X(20F, offsetX, scale), Y(27F, offsetY, scale), X(30F, offsetX, scale), Y(15F, offsetY, scale));
                    }
                    break;
                }

                case IconGlyph.Pause:
                {
                    using (SolidBrush brush = new SolidBrush(controlColor))
                    {
                        graphics.FillRectangle(brush, X(15F, offsetX, scale), Y(13F, offsetY, scale), Scaled(4F, scale), Scaled(18F, scale));
                        graphics.FillRectangle(brush, X(25F, offsetX, scale), Y(13F, offsetY, scale), Scaled(4F, scale), Scaled(18F, scale));
                    }
                    break;
                }

                case IconGlyph.Play:
                {
                    PointF[] triangle = new PointF[3];
                    triangle[0] = new PointF(X(17F, offsetX, scale), Y(13F, offsetY, scale));
                    triangle[1] = new PointF(X(17F, offsetX, scale), Y(31F, offsetY, scale));
                    triangle[2] = new PointF(X(31F, offsetX, scale), Y(22F, offsetY, scale));
                    using (SolidBrush brush = new SolidBrush(controlColor))
                    using (Pen pen = new Pen(controlOutline, Math.Max(1F, scale)))
                    {
                        graphics.FillPolygon(brush, triangle);
                        graphics.DrawPolygon(pen, triangle);
                    }
                    break;
                }

                case IconGlyph.Spinner:
                {
                    using (Pen pen = new Pen(bodyColor, Scaled(3F, scale)))
                    {
                        graphics.DrawArc(pen, X(16F, offsetX, scale), Y(14F, offsetY, scale), Scaled(12F, scale), Scaled(16F, scale), 20F, 300F);
                    }
                    break;
                }
            }
        }

        private static float X(float value, float offset, float scale)
        {
            return offset + (value * scale);
        }

        private static float Y(float value, float offset, float scale)
        {
            return offset + (value * scale);
        }

        private static float Scaled(float value, float scale)
        {
            return Math.Max(1F, value * scale);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && scaledImage != null)
            {
                scaledImage.Dispose();
                scaledImage = null;
            }
            base.Dispose(disposing);
        }

        // Aciona o botão como um clique (usado pela automação e por leitores de
        // tela através do objeto de acessibilidade).
        public void PerformClick()
        {
            if (!Enabled) return;
            OnClick(EventArgs.Empty);
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new IconButtonAccessibleObject(this);
        }

        private sealed class IconButtonAccessibleObject : ControlAccessibleObject
        {
            private readonly IconButton owner;

            public IconButtonAccessibleObject(IconButton owner)
                : base(owner)
            {
                this.owner = owner;
            }

            public override string Name
            {
                get { return owner.AccessibleName ?? ""; }
                set { owner.AccessibleName = value; }
            }

            public override AccessibleRole Role
            {
                get { return AccessibleRole.PushButton; }
            }

            public override string DefaultAction
            {
                get { return "Pressionar"; }
            }

            // Sem isto, o InvokePattern exposto pelo WinForms não aciona o
            // evento Click do controle.
            public override void DoDefaultAction()
            {
                owner.PerformClick();
            }
        }

        internal static GraphicsPath RoundedRectanglePath(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static Color Blend(Color from, Color to, float ratio)
        {
            ratio = Math.Max(0F, Math.Min(1F, ratio));
            return Color.FromArgb(
                from.A,
                (int)(from.R + ((to.R - from.R) * ratio)),
                (int)(from.G + ((to.G - from.G) * ratio)),
                (int)(from.B + ((to.B - from.B) * ratio)));
        }
    }

    // Timeline de áudio: play/pause, barra de progresso clicável e tempo.
    // Usada na pré-visualização do remetente e no popup de recebimento.
    internal sealed class AudioTrackPanel : Panel
    {
        private const int TimelineHeight = 6;
        private const int KnobRadius = 7;

        private readonly IconButton playButton;
        private readonly Label timeLabel;
        private readonly Button removeButton;
        private readonly WavePlayer player = new WavePlayer();
        private readonly System.Windows.Forms.Timer ticker;
        private bool dragging;
        private bool loadFailed;
        private bool popupColors;
        private int playButtonSize = 40;

        public event EventHandler RemoveRequested;
        public event EventHandler PlaybackFailed;

        public AudioTrackPanel(AudioPayload audio, bool allowRemove)
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw,
                true);
            BackColor = Color.White;

            string loadError = "";
            if (audio != null && audio.WavBytes != null)
            {
                loadFailed = !player.Load(audio.WavBytes, out loadError);
            }
            else
            {
                loadFailed = true;
            }

            // Mesmo botao amarelo do SIG (barras quando toca, triangulo quando
            // esta parado).
            playButton = new IconButton();
            playButton.Glyph = IconGlyph.Play;
            playButton.CircleColor = Color.FromArgb(242, 207, 55);
            playButton.CircleOutline = Color.FromArgb(196, 157, 0);
            playButton.Size = new Size(40, 40);
            playButton.AccessibleName = "Reproduzir";
            playButton.Enabled = !loadFailed;
            playButton.Click += delegate { TogglePlayback(); };
            Controls.Add(playButton);

            timeLabel = new Label();
            timeLabel.AutoSize = false;
            timeLabel.TextAlign = ContentAlignment.MiddleRight;
            timeLabel.ForeColor = Color.FromArgb(75, 85, 99);
            timeLabel.Font = new Font("Segoe UI", 8.5F);
            timeLabel.Text = loadFailed
                ? "áudio indisponível"
                : Format(0) + " / " + Format(player.DurationSeconds);
            timeLabel.Size = new Size(92, 20);
            Controls.Add(timeLabel);

            if (allowRemove)
            {
                removeButton = new Button();
                removeButton.Text = "Remover";
                removeButton.Size = new Size(78, 24);
                removeButton.BackColor = Color.FromArgb(55, 65, 81);
                removeButton.ForeColor = Color.White;
                removeButton.FlatStyle = FlatStyle.Flat;
                removeButton.FlatAppearance.BorderSize = 0;
                removeButton.Cursor = Cursors.Hand;
                removeButton.Click += delegate
                {
                    EventHandler handler = RemoveRequested;
                    if (handler != null) handler(this, EventArgs.Empty);
                };
                Controls.Add(removeButton);
            }

            ticker = new System.Windows.Forms.Timer();
            ticker.Interval = 100;
            ticker.Tick += delegate { Tick(); };
            ticker.Start();

            Resize += delegate { LayoutControls(); };
            LayoutControls();
        }

        // No popup o painel fica sobre o fundo escuro da janela, sem quadro
        // branco: o tempo passa a usar cor clara e o botão fica menor.
        public void UsePopupColors(int buttonSize)
        {
            popupColors = true;
            playButtonSize = Math.Max(20, buttonSize);
            BackColor = Color.FromArgb(31, 41, 55);
            timeLabel.ForeColor = Color.FromArgb(209, 213, 219);
            timeLabel.Font = new Font("Segoe UI", 8F);
            timeLabel.Size = new Size(84, 18);
            playButton.Size = new Size(playButtonSize, playButtonSize);
            LayoutControls();
            Invalidate();
        }

        public double PositionSeconds
        {
            get { return player.PositionSeconds; }
        }

        public double DurationSeconds
        {
            get { return player.DurationSeconds; }
        }

        // Para a reprodução (usado quando o painel sai da tela).
        public void StopPlayback()
        {
            try
            {
                player.Stop();
            }
            catch
            {
                // A parada nunca pode derrubar a janela.
            }
            UpdatePlayGlyph();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (ticker != null)
                {
                    ticker.Stop();
                    ticker.Dispose();
                }
                player.Dispose();
            }
            base.Dispose(disposing);
        }

        private void LayoutControls()
        {
            playButton.Location = new Point(4, Math.Max(2, (ClientSize.Height - playButton.Height) / 2));
            int removeWidth = removeButton == null ? 0 : removeButton.Width + 8;
            if (removeButton != null)
            {
                removeButton.Location = new Point(
                    Math.Max(playButton.Right + 60, ClientSize.Width - removeButton.Width - 4),
                    Math.Max(2, (ClientSize.Height - removeButton.Height) / 2));
            }
            timeLabel.Location = new Point(
                Math.Max(playButton.Right + 4, ClientSize.Width - removeWidth - timeLabel.Width - 6),
                Math.Max(2, (ClientSize.Height - timeLabel.Height) / 2));
            Invalidate();
        }

        private int TimelineLeft
        {
            get { return playButton.Right + 10; }
        }

        private int TimelineRight
        {
            get
            {
                int right = timeLabel.Left - 10;
                if (right < TimelineLeft + 20) right = TimelineLeft + 20;
                return right;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // A trilha fica mais discreta sobre o fundo escuro do popup.
            Color trackColor = popupColors
                ? Color.FromArgb(75, 85, 99)
                : Color.FromArgb(226, 232, 240);

            int width = TimelineRight - TimelineLeft;
            int top = (ClientSize.Height - TimelineHeight) / 2;
            double duration = Math.Max(0.001, player.DurationSeconds);
            double position = Math.Min(player.PositionSeconds, duration);
            int filled = (int)(width * (position / duration));

            using (SolidBrush trackBrush = new SolidBrush(trackColor))
            using (SolidBrush progressBrush = new SolidBrush(
                loadFailed ? Color.FromArgb(156, 163, 175) : Color.FromArgb(37, 99, 235)))
            using (GraphicsPath trackPath = IconButton.RoundedRectanglePath(
                new Rectangle(TimelineLeft, top, width, TimelineHeight),
                TimelineHeight / 2))
            {
                graphics.FillPath(trackBrush, trackPath);
                if (filled > 2)
                {
                    using (GraphicsPath progressPath = IconButton.RoundedRectanglePath(
                        new Rectangle(TimelineLeft, top, filled, TimelineHeight),
                        TimelineHeight / 2))
                    {
                        graphics.FillPath(progressBrush, progressPath);
                    }
                }
                graphics.FillEllipse(
                    progressBrush,
                    TimelineLeft + filled - KnobRadius,
                    (ClientSize.Height / 2) - KnobRadius,
                    KnobRadius * 2,
                    KnobRadius * 2);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (loadFailed || e.X < TimelineLeft - KnobRadius || e.X > TimelineRight + KnobRadius)
            {
                return;
            }
            dragging = true;
            SeekTo(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SeekTo(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        private void SeekTo(int x)
        {
            int width = Math.Max(1, TimelineRight - TimelineLeft);
            double ratio = (x - TimelineLeft) / (double)width;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            player.Seek(ratio * player.DurationSeconds);
            UpdatePlayGlyph();
            Invalidate();
        }

        private void TogglePlayback()
        {
            string error;
            bool ok;
            if (player.IsPlaying)
            {
                ok = player.Pause(out error);
            }
            else if (player.IsPaused)
            {
                ok = player.Resume(out error);
            }
            else
            {
                player.Restart();
                ok = player.Play(out error);
            }

            if (!ok)
            {
                EventHandler handler = PlaybackFailed;
                if (handler != null)
                {
                    PlaybackFailedEventArgs args = new PlaybackFailedEventArgs();
                    args.ErrorMessage = error;
                    handler(this, args);
                }
            }
            UpdatePlayGlyph();
        }

        private void Tick()
        {
            if (ticker != null && !player.IsPlaying && !player.IsPaused)
            {
                ticker.Stop();
            }
            if (!IsHandleCreated || IsDisposed) return;
            if (player.IsPlaying)
            {
                Invalidate();
            }
            timeLabel.Text = loadFailed
                ? "áudio indisponível"
                : Format(player.PositionSeconds) + " / " + Format(player.DurationSeconds);
            UpdatePlayGlyph();
        }

        private void UpdatePlayGlyph()
        {
            IconGlyph expected = player.IsPlaying ? IconGlyph.Pause : IconGlyph.Play;
            if (playButton.Glyph != expected)
            {
                playButton.Glyph = expected;
                playButton.AccessibleName =
                    expected == IconGlyph.Pause ? "Pausar" : "Reproduzir";
                playButton.Invalidate();
            }
            // O circulo muda de cor junto: amarelo do SIG sempre, para manter o
            // mesmo visual do aplicativo de referencia.
        }

        private static string Format(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int total = (int)Math.Round(seconds);
            return (total / 60).ToString(CultureInfo.InvariantCulture) + ":" +
                (total % 60).ToString("00", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class PlaybackFailedEventArgs : EventArgs
    {
        public string ErrorMessage;
    }

    // Histórico de mensagens recebidas. Substitui a caixa de texto simples:
    // as linhas de áudio trazem um botão de play e a transcrição ao lado.
    // Contorno das caixas de lista: a mesma borda da caixa de mensagem
    // (o cinza do FixedSingle) desenhada à mão, porque o retângulo para antes
    // da barra de rolagem — o FixedSingle a envolveria.
    internal static class BoxBorder
    {
        public static readonly Color LineColor = Color.FromArgb(100, 100, 100);

        public static void Draw(Graphics graphics, Control target, int reserveRight)
        {
            if (graphics == null || target == null) return;
            // 1 px a mais à direita: sem isso a linha direita fica escondida
            // atrás da barra de rolagem / do painel de conteúdo.
            int width = target.ClientSize.Width - Math.Max(0, reserveRight) - 1;
            int height = target.ClientSize.Height;
            if (width <= 1 || height <= 1) return;
            using (Pen pen = new Pen(LineColor))
            {
                graphics.DrawRectangle(pen, 0, 0, width - 1, height - 1);
            }
        }
    }

    // Evento de deleção pedido pelo menu de contexto de uma linha.
    internal sealed class InboxDeleteEventArgs : EventArgs
    {
        public long Seq;
        public string OperationId;
        public string Address;
        public bool ForEveryone;
    }

    internal sealed class InboxPanel : Panel
    {
        private const int PlayerSize = 22;
        private InboxAudioRow activeRow;
        private Font rowFont;
        private readonly VScrollBar scrollBar;
        private readonly Panel viewport;
        // Recorte interno: mantém o conteúdo dentro da margem de 1 px, para as
        // linhas (brancas) nunca cobrirem as bordas de cima e de baixo ao rolar.
        private readonly Panel clip;
        // Painel que guarda as linhas: a rolagem só o desloca (nada de refazer
        // o layout de todas as linhas a cada passo do arrasto).
        private readonly Panel content;
        private readonly ContextMenuStrip sharedMenu;
        private readonly ToolStripMenuItem menuTranscription;
        private readonly ToolStripControlHost menuThumbnailItem;
        private readonly PictureBox menuThumbnailPreview;
        private readonly ToolStripMenuItem menuCopy;
        private readonly ToolStripMenuItem menuSaveAs;
        private readonly ToolStripMenuItem menuDelete;
        private readonly ToolStripMenuItem menuDeleteAll;
        private readonly Font menuItalicFont;
        private bool layingOut;
        private bool bulkLoad;
        // Dia da última divisória do histórico ("14/09/2026"): muda de dia,
        // entra uma linha de data antes das mensagens seguintes.
        private DateTime ultimaData = DateTime.MinValue;
        // Altura acumulada das linhas: permite acrescentar uma linha nova sem
        // varrer o histórico inteiro (custo constante por mensagem).
        private int lastBottom;

        // Durante a carga do histórico, o layout roda UMA vez no fim: sem isso
        // cada linha acrescentada disparava um layout completo e a subida
        // ficava O(n^2) (medido: 16 s com 128 entradas).
        public void BeginBulkLoad()
        {
            bulkLoad = true;
            content.SuspendLayout();
            clip.SuspendLayout();
        }

        public void EndBulkLoad()
        {
            bulkLoad = false;
            content.ResumeLayout(false);
            clip.ResumeLayout(false);
            LayoutRows();
            ScrollToBottom();
        }

        // Cor das mensagens enviadas (verde).
        internal static readonly Color SentColor = Color.FromArgb(22, 128, 61);

        public event EventHandler<InboxDeleteEventArgs> DeleteRequested;

        public InboxPanel()
        {
            // A rolagem é própria e fica FORA do contorno. O conteúdo vive em um
            // painel interno com 1 px de margem, para nenhuma linha encostar na
            // borda desenhada (era o pedaço de mensagem sobre o contorno).
            AutoScroll = false;
            BackColor = Color.White;
            BorderStyle = BorderStyle.None;
            // Sem padding: a barra de rolagem fica colada na beirada direita,
            // fora do contorno (o contorno é o fundo do viewport, abaixo).
            Padding = new Padding(0);
            rowFont = new Font("Segoe UI", 9.5F);

            viewport = new Panel();
            viewport.Dock = DockStyle.Fill;
            // O contorno é o fundo do viewport aparecendo na margem de 1 px:
            // desenhado à mão ele seria coberto pelo conteúdo. Como a barra de
            // rolagem fica fora do viewport, o retângulo não a abraça, igual à
            // caixa de mensagem.
            viewport.BackColor = BoxBorder.LineColor;
            viewport.Padding = new Padding(1);
            Controls.Add(viewport);

            clip = new Panel();
            clip.Location = new Point(1, 1);
            clip.BackColor = Color.White;
            viewport.Controls.Add(clip);

            content = new Panel();
            content.Location = new Point(0, 0);
            content.BackColor = Color.White;
            clip.Controls.Add(content);

            // A faixa da barra é sempre reservada (painel branco à direita), com
            // a barra dentro dela: assim o contorno termina na mesma coluna da
            // caixa de mensagem, esteja a barra visível ou não.
            Panel scrollStrip = new Panel();
            scrollStrip.Dock = DockStyle.Right;
            scrollStrip.Width = SystemInformation.VerticalScrollBarWidth;
            scrollStrip.BackColor = Color.White;
            Controls.Add(scrollStrip);

            scrollBar = new VScrollBar();
            scrollBar.Dock = DockStyle.Fill;
            scrollBar.Width = SystemInformation.VerticalScrollBarWidth;
            scrollBar.SmallChange = 24;
            scrollBar.Visible = false;
            scrollBar.Scroll += delegate { ApplyScroll(); };
            scrollStrip.Controls.Add(scrollBar);

            sharedMenu = new ContextMenuStrip();
            sharedMenu.Font = rowFont;
            menuItalicFont = new Font(rowFont, FontStyle.Italic);
            // Miniatura da imagem: PictureBox com Zoom (mesmo porte da prévia
            // do anexo) hospedado no menu. Fica FORA da coluna de imagens —
            // assim o menu mantém o tamanho normal e só a miniatura cresce.
            menuThumbnailPreview = new PictureBox();
            menuThumbnailPreview.Size = new Size(56, 56);
            menuThumbnailPreview.SizeMode = PictureBoxSizeMode.Zoom;
            menuThumbnailPreview.BackColor = Color.White;
            menuThumbnailPreview.Cursor = Cursors.Hand;
            menuThumbnailPreview.Click += delegate
            {
                InboxRowBase alvoMiniatura = RowUnderMenu();
                sharedMenu.Close();
                if (alvoMiniatura != null) alvoMiniatura.MenuPrimaryAction();
            };
            menuThumbnailItem = new ToolStripControlHost(menuThumbnailPreview);
            menuThumbnailItem.AutoSize = false;
            menuThumbnailItem.Size = new Size(132, 64);
            menuThumbnailItem.Margin = new Padding(6, 4, 6, 2);
            menuThumbnailPreview.Location = new Point(38, 4);
            menuThumbnailItem.Visible = false;
            menuTranscription = new ToolStripMenuItem("");
            menuTranscription.Font = menuItalicFont;
            menuTranscription.ForeColor = Color.FromArgb(75, 85, 99);
            menuTranscription.Click += SharedMenuPrimary;
            menuCopy = new ToolStripMenuItem("Copiar");
            menuCopy.Click += SharedMenuCopy;
            menuSaveAs = new ToolStripMenuItem("Salvar como");
            menuSaveAs.Click += SharedMenuSaveAs;
            menuDelete = new ToolStripMenuItem("Deletar");
            menuDelete.Click += SharedMenuDelete;
            menuDeleteAll = new ToolStripMenuItem("Deletar para todos");
            menuDeleteAll.Click += SharedMenuDeleteAll;
            // Ordem pedida: transcrição/miniatura/nome, Copiar (imagem),
            // Salvar como, Deletar e Deletar para todos (só enviadas).
            sharedMenu.Items.Add(menuThumbnailItem);
            sharedMenu.Items.Add(menuTranscription);
            sharedMenu.Items.Add(menuCopy);
            sharedMenu.Items.Add(menuSaveAs);
            sharedMenu.Items.Add(new ToolStripSeparator());
            sharedMenu.Items.Add(menuDelete);
            sharedMenu.Items.Add(menuDeleteAll);
            sharedMenu.Opening += delegate { ConfigureSharedMenu(); };
        }

        // O layout pode rodar antes do viewport existir (o próprio construtor
        // adiciona controles), então a largura/altura precisam de guarda.
        private int ContentWidth
        {
            get
            {
                // Mesma fonte do LayoutRows (área de recorte): se as duas
                // contas divergirem, a linha recém-chegada fica com largura
                // diferente das demais até o próximo layout completo.
                if (clip != null && clip.ClientSize.Width > 40)
                    return Math.Max(120, clip.ClientSize.Width);
                int width = viewport == null ? ClientSize.Width : viewport.ClientSize.Width;
                return Math.Max(120, width - 20);
            }
        }

        private int ViewportHeight
        {
            get { return viewport == null ? ClientSize.Height : viewport.ClientSize.Height; }
        }

        // Formato das linhas: recebida "19:21, LUIZ -> mensagem" e enviada
        // "mensagem -> LUIZ, 19:22". A hora fica sempre em HH:mm.
        internal static string FormatLine(string who, string body, string time, bool sent)
        {
            string hora = String.IsNullOrEmpty(time) ? "" :
                (time.Length >= 5 ? time.Substring(0, 5) : time);
            string nome = who ?? "";
            string conteudo = body ?? "";
            return sent
                ? conteudo + " -> " + nome + ", " + hora
                : hora + ", " + nome + " -> " + conteudo;
        }

        // Há linhas na tela? (usado para não refazer a carga sem necessidade)
        public bool HasRows
        {
            get { return content != null && content.Controls.Count > 0; }
        }

        // Linha "carregar mais mensagens", no topo do histórico.
        public void AppendLoadMore(EventHandler onClick)
        {
            if (content == null) return;
            Button more = new Button();
            more.Text = "Carregar mais mensagens";
            more.FlatStyle = FlatStyle.Flat;
            more.FlatAppearance.BorderColor = Color.FromArgb(209, 213, 219);
            more.BackColor = Color.White;
            more.ForeColor = Color.FromArgb(75, 85, 99);
            more.Font = rowFont;
            more.Height = 26;
            more.Cursor = Cursors.Hand;
            more.AccessibleName = "Carregar mais mensagens";
            if (onClick != null) more.Click += onClick;
            AddRow(more);
        }

        // Divisória de data (igual à do WhatsApp): antes da primeira mensagem
        // de cada dia entra uma linha com a data; as mensagens seguem normais.
        public void GarantirDivisaoDeData(DateTime data)
        {
            if (content == null) return;
            DateTime dia = data.Date;
            if (dia == ultimaData) return;
            ultimaData = dia;

            Label separador = new Label();
            separador.Text = dia.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            separador.TextAlign = ContentAlignment.MiddleCenter;
            separador.AutoSize = false;
            separador.Font = rowFont;
            separador.ForeColor = Color.FromArgb(107, 114, 128);
            separador.BackColor = Color.FromArgb(243, 244, 246);
            separador.Height = 26;
            AddRow(separador);
        }

        // Texto do histórico: recebida alinhada à esquerda, enviada à direita.
        public InboxTextRow AppendMessage(
            string who,
            string text,
            string time,
            bool sent,
            long seq,
            string operationId)
        {
            // Mensagem ao vivo: garante a divisória do dia antes dela.
            if (!bulkLoad) GarantirDivisaoDeData(DateTime.Now);
            InboxTextRow row = new InboxTextRow(who, text, time, sent, rowFont);
            row.Seq = seq;
            row.OperationId = operationId;
            row.Sent = sent;
            AttachMenu(row, seq, operationId, sent, "");
            AddRow(row);
            return row;
        }

        // Linhas de texto simples (avisos e o histórico antigo).
        public void AppendText(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (string line in lines)
            {
                if (line.Length == 0) continue;
                Label label = new Label();
                label.AutoSize = true;
                label.Font = rowFont;
                label.ForeColor = Color.FromArgb(31, 41, 55);
                label.Text = line;
                AddRow(label);
            }
        }

        public InboxImageRow AppendImage(
            string who,
            string body,
            string time,
            byte[] imageBytes,
            string filePath,
            bool asFile = false,
            string fileName = "",
            int fileCount = 1,
            string fileNames = "")
        {
            // Imagem/arquivo ao vivo: garante a divisória do dia antes dela.
            if (!bulkLoad) GarantirDivisaoDeData(DateTime.Now);
            InboxImageRow row = asFile
                ? new InboxImageRow(who, body, imageBytes, filePath, PlayerSize, rowFont, true, fileName, fileCount, fileNames)
                : new InboxImageRow(who, body, imageBytes, filePath, PlayerSize, rowFont);
            row.SetRowTime(time);
            AddRow(row);
            return row;
        }

        public InboxAudioRow AppendAudio(
            string who,
            string body,
            string time,
            AudioPayload audio,
            string operationId)
        {
            // Áudio ao vivo: garante a divisória do dia antes dele.
            if (!bulkLoad) GarantirDivisaoDeData(DateTime.Now);
            InboxAudioRow row = new InboxAudioRow(who, body, audio, operationId, PlayerSize, rowFont);
            row.SetRowTime(time);
            row.PlayRequested += delegate(object sender, EventArgs e)
            {
                InboxAudioRow requested = sender as InboxAudioRow;
                if (requested == null) return;
                if (activeRow != null && activeRow != requested)
                {
                    activeRow.StopPlayback();
                }
                activeRow = requested.IsPlaying ? requested : null;
            };
            AddRow(row);
            return row;
        }

        // Menu de contexto UNICO para todas as linhas: antes cada linha criava
        // o seu (uma janela por linha), o que pesa na abertura com historico
        // grande. Os itens sao configurados no Opening, conforme a linha.
        public void AttachMenu(
            Control row,
            long seq,
            string operationId,
            bool sent,
            string transcription)
        {
            InboxRowBase baseRow = row as InboxRowBase;
            if (baseRow == null) return;
            baseRow.Seq = seq;
            baseRow.OperationId = operationId;
            baseRow.Sent = sent;
            baseRow.TranscriptionText = transcription;
            AtribuirMenu(baseRow, sharedMenu);
        }

        // O menu também precisa abrir ao clicar com o botão direito NOS FILHOS
        // (o ícone de play/imagem, os textos): controles filhos não herdam o
        // menu do painel, então cada um recebe o mesmo menu compartilhado.
        private static void AtribuirMenu(Control pai, ContextMenuStrip menu)
        {
            pai.ContextMenuStrip = menu;
            foreach (Control filho in pai.Controls)
            {
                AtribuirMenu(filho, menu);
            }
        }

        private InboxRowBase RowUnderMenu()
        {
            Control atual = sharedMenu.SourceControl;
            while (atual != null)
            {
                InboxRowBase linha = atual as InboxRowBase;
                if (linha != null) return linha;
                atual = atual.Parent;
            }
            return null;
        }

        private void ConfigureSharedMenu()
        {
            InboxRowBase linha = RowUnderMenu();
            if (linha == null) return;

            // Primeiro item: miniatura (imagem) OU transcrição (áudio) OU
            // nome do arquivo (clipe). Vale para recebidas E enviadas.
            Image miniatura = linha.MenuThumbnail;
            menuThumbnailItem.Visible = miniatura != null;
            if (miniatura != null)
            {
                menuThumbnailPreview.Image = miniatura;
            }

            string principal = linha.MenuPrimaryText;
            bool temTexto = miniatura == null && !String.IsNullOrEmpty(principal);
            menuTranscription.Visible = temTexto;
            if (temTexto)
            {
                bool audio = linha is InboxAudioRow;
                menuTranscription.Font = audio ? menuItalicFont : sharedMenu.Font;
                menuTranscription.Text = audio ? "\"" + principal + "\"" : principal;
                menuTranscription.Enabled = true;
            }
            else
            {
                menuTranscription.Text = "";
                menuTranscription.Enabled = false;
            }

            menuCopy.Visible = linha.CanCopy;
            menuSaveAs.Visible = linha.CanSaveAs;
            menuDeleteAll.Visible = linha.Sent && !String.IsNullOrEmpty(linha.OperationId);
        }

        private void SharedMenuDelete(object sender, EventArgs e)
        {
            InboxRowBase linha = RowUnderMenu();
            if (linha == null) return;
            RaiseDelete(linha.Seq, linha.OperationId, linha.Address, false);
        }

        private void SharedMenuDeleteAll(object sender, EventArgs e)
        {
            InboxRowBase linha = RowUnderMenu();
            if (linha == null) return;
            RaiseDelete(linha.Seq, linha.OperationId, linha.Address, true);
        }

        // Clique no primeiro item: copia a transcrição (áudio) ou abre a
        // imagem/arquivo no aplicativo padrão do sistema.
        private void SharedMenuPrimary(object sender, EventArgs e)
        {
            InboxRowBase linha = RowUnderMenu();
            if (linha == null) return;
            linha.MenuPrimaryAction();
        }

        // Copiar (só quando a célula tem imagem anexa): vai para a área de
        // transferência, pronta para colar no Word, Paint etc.
        private void SharedMenuCopy(object sender, EventArgs e)
        {
            InboxRowBase linha = RowUnderMenu();
            if (linha == null) return;
            linha.CopyContent();
        }

        private void SharedMenuSaveAs(object sender, EventArgs e)
        {
            InboxRowBase linha = RowUnderMenu();
            if (linha == null) return;
            linha.SaveAs();
        }

        private void RaiseDelete(long seq, string operationId, string address, bool forEveryone)
        {
            if (DeleteRequested == null) return;
            InboxDeleteEventArgs args = new InboxDeleteEventArgs();
            args.Seq = seq;
            args.OperationId = operationId;
            args.Address = address;
            args.ForEveryone = forEveryone;
            DeleteRequested(this, args);
        }

        // Remove a linha da tela (usado ao deletar e no "deletar para todos").
        public bool RemoveRow(Control row)
        {
            if (row == null) return false;
            if (activeRow == row as InboxAudioRow)
            {
                activeRow = null;
            }
            InboxAudioRow audioRow = row as InboxAudioRow;
            if (audioRow != null) audioRow.StopPlayback();
            content.Controls.Remove(row);
            row.Dispose();
            LayoutRows();
            return true;
        }

        public bool RemoveBySeq(long seq)
        {
            foreach (Control control in Snapshot())
            {
                InboxRowBase baseRow = control as InboxRowBase;
                if (baseRow != null && baseRow.Seq == seq) return RemoveRow(control);
            }
            return false;
        }

        public bool RemoveByOperationId(string operationId)
        {
            if (String.IsNullOrEmpty(operationId)) return false;
            bool removed = false;
            foreach (Control control in Snapshot())
            {
                InboxRowBase baseRow = control as InboxRowBase;
                if (baseRow != null && String.Equals(baseRow.OperationId, operationId,
                    StringComparison.Ordinal))
                {
                    removed = RemoveRow(control) || removed;
                }
            }
            return removed;
        }

        private Control[] Snapshot()
        {
            if (content == null) return new Control[0];
            Control[] children = new Control[content.Controls.Count];
            content.Controls.CopyTo(children, 0);
            return children;
        }

        public void Clear()
        {
            if (content == null) return;
            // Ao limpar, a próxima carga insere as divisórias de data de novo.
            ultimaData = DateTime.MinValue;
            if (activeRow != null)
            {
                activeRow.StopPlayback();
                activeRow = null;
            }
            foreach (Control control in Snapshot())
            {
                InboxAudioRow audioRow = control as InboxAudioRow;
                if (audioRow != null) audioRow.StopPlayback();
                content.Controls.Remove(control);
                control.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && rowFont != null)
            {
                rowFont.Dispose();
                rowFont = null;
            }
            base.Dispose(disposing);
        }

        private void AddRow(Control row)
        {
            if (content == null) return;
            content.SuspendLayout();
            row.Width = ContentWidth;
            content.Controls.Add(row);
            content.ResumeLayout();
            if (bulkLoad) return;

            // Caminho rápido: a linha nova vai no fim e só ela é posicionada.
            // O layout completo fica para quando a largura muda ou algo sai.
            row.PerformLayout();   // grade já no tamanho final (senão fica defasada)
            row.Location = new Point(0, lastBottom);
            lastBottom += row.Height + 2;
            content.Height = Math.Max(0, lastBottom);
            UpdateScrollAfterAppend();
            ScrollToBottom();
        }

        // Ajuste O(1) da barra ao acrescentar uma linha (sem varrer o conteúdo).
        private void UpdateScrollAfterAppend()
        {
            int viewportHeight = ViewportHeight;
            int overflow = Math.Max(0, lastBottom - viewportHeight);
            bool needed = overflow > 0;
            scrollBar.LargeChange = Math.Max(1, viewportHeight / 4);
            scrollBar.Maximum = overflow + scrollBar.LargeChange - 1;
            int maximum = Math.Max(0, scrollBar.Maximum - scrollBar.LargeChange + 1);
            if (scrollBar.Value > maximum) scrollBar.Value = maximum;
            if (scrollBar.Visible != needed) scrollBar.Visible = needed;
        }

        // Mantém a última mensagem visível, como em um aplicativo de conversa.
        public void ScrollToBottom()
        {
            if (scrollBar == null || !scrollBar.Visible) return;
            SetScrollValue(scrollBar.Maximum);
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            LayoutRows();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (scrollBar != null && scrollBar.Visible)
            {
                int delta = e.Delta > 0 ? -scrollBar.SmallChange * 3 : scrollBar.SmallChange * 3;
                SetScrollValue(scrollBar.Value + delta);
            }
        }

        private void SetScrollValue(int value)
        {
            int maximum = Math.Max(0, scrollBar.Maximum - scrollBar.LargeChange + 1);
            int clamped = Math.Max(0, Math.Min(value, maximum));
            if (scrollBar.Value != clamped)
            {
                scrollBar.Value = clamped;
            }
            ApplyScroll();
        }

        // Uma operação por passo de rolagem: o painel de conteúdo desloca
        // inteiro, com as linhas dentro, em vez de reposicionar cada linha.
        private void ApplyScroll()
        {
            if (content == null || scrollBar == null) return;
            int offset = scrollBar.Visible ? scrollBar.Value : 0;
            int top = -offset;
            if (content.Top != top)
            {
                content.Top = top;
            }
            if (content.Left != 0)
            {
                content.Left = 0;
            }
        }

        // Posiciona as linhas e ajusta a barra em passadas curtas: a largura
        // útil depende da barra, e a altura do conteúdo depende da largura.
        // Refaz o layout do conteúdo (chamado quando algo muda: nova linha,
        // remoção ou redimensionamento). A rolagem em si não passa por aqui.
        private void LayoutRows()
        {
            if (layingOut || viewport == null || clip == null || content == null || scrollBar == null) return;
            layingOut = true;
            // Guarda se a vista já estava no fim: depois do layout ela volta ao
            // fim — sem isso maximizar/restaurar largava a rolagem no meio do
            // histórico, mesmo com as linhas idênticas.
            bool estavaNoFundo = scrollBar.Value >=
                Math.Max(0, scrollBar.Maximum - scrollBar.LargeChange + 1);
            // Sem o SuspendLayout, cada alteração de largura/posição dispara um
            // relayout completo do container: com o histórico cheio isso virava
            // O(n²) e custava SEGUNDOS por mensagem (medido: 5,9 s).
            clip.SuspendLayout();
            content.SuspendLayout();
            try
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    bool visibleBefore = scrollBar.Visible;
                    clip.Bounds = new Rectangle(
                        1,
                        1,
                        Math.Max(1, viewport.ClientSize.Width - 2),
                        Math.Max(1, viewport.ClientSize.Height - 2));
                    // Largura cheia do recorte: a enviada encosta na direita na
                    // mesma medida em que a recebida encosta na esquerda.
                    int width = Math.Max(120, clip.ClientSize.Width);
                    content.Width = Math.Max(120, clip.ClientSize.Width);
                    int top = 0;
                    foreach (Control control in Snapshot())
                    {
                        // Só escreve quando o valor muda: cada escrita dispara
                        // trabalho de layout/repintura no WinForms.
                        if (control.Width != width) control.Width = width;
                        InboxRowBase baseRow = control as InboxRowBase;
                        if (baseRow != null)
                        {
                            baseRow.AjustarGrade();
                            baseRow.LayoutRow();
                        }
                        if (control.Top != top || control.Left != 0)
                        {
                            control.Location = new Point(0, top);
                        }
                        top += control.Height + 2;
                    }

                    content.Height = Math.Max(0, top);
                    lastBottom = top;
                    int viewportHeight = ViewportHeight;
                    int overflow = Math.Max(0, content.Height - viewportHeight);
                    bool needed = overflow > 0;

                    scrollBar.LargeChange = Math.Max(1, viewportHeight / 4);
                    scrollBar.Maximum = overflow + scrollBar.LargeChange - 1;
                    int maximum = Math.Max(0, scrollBar.Maximum - scrollBar.LargeChange + 1);
                    if (scrollBar.Value > maximum) scrollBar.Value = maximum;

                    if (needed != visibleBefore)
                    {
                        scrollBar.Visible = needed;
                        continue;
                    }
                    break;
                }
                ApplyScroll();
                // Se a vista já estava no fim, continua no fim depois do layout
                // (maximizar/restaurar deixa de mexer na posição da rolagem).
                if (estavaNoFundo) ScrollToBottom();
            }
            finally
            {
                // true = executa o layout pendente agora: sem isso a grade e
                // as células continuavam com o tamanho antigo até um resize da
                // janela ("maximizar e voltar alinha as mensagens").
                content.ResumeLayout(true);
                clip.ResumeLayout(true);
                layingOut = false;
            }
        }


    }

    // Base das linhas que o menu de contexto pode apagar.
    // Nomes sugeridos no "Salvar como", no padrão do usuário:
    //     audio_recebido_de_LUIZ_as_13-54_dia_14-09-2026.wav
    // (o ':' do horário vira '-' porque o Windows não aceita ':' no nome).
    internal static class NomeDeMidia
    {
        public static string Gerar(
            string prefixo,
            bool sent,
            string quem,
            string hora,
            DateTime quando,
            string extensao)
        {
            return prefixo +
                (sent ? "_enviado_para_" : "_recebido_de_") +
                Limpar(quem) + "_as_" + Limpar(hora) + "_dia_" +
                quando.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) +
                extensao;
        }

        // Extensão provável pelo conteúdo (registros antigos sem nome).
        public static string ExtensaoDoConteudo(byte[] conteudo)
        {
            if (conteudo == null || conteudo.Length < 2) return ".bin";
            if (conteudo.Length >= 8 && conteudo[0] == 0x89 && conteudo[1] == 0x50 &&
                conteudo[2] == 0x4E && conteudo[3] == 0x47)
            {
                return ".png";
            }
            if (conteudo.Length >= 4 && conteudo[0] == 0x25 && conteudo[1] == 0x50 &&
                conteudo[2] == 0x44 && conteudo[3] == 0x46)
            {
                return ".pdf";
            }
            if (conteudo.Length >= 2 && conteudo[0] == 0xFF && conteudo[1] == 0xD8)
            {
                return ".jpg";
            }
            if (conteudo.Length >= 4 && conteudo[0] == 0x47 && conteudo[1] == 0x49 &&
                conteudo[2] == 0x46)
            {
                return ".gif";
            }
            if (conteudo.Length >= 2 && conteudo[0] == 0x42 && conteudo[1] == 0x4D)
            {
                return ".bmp";
            }
            if (conteudo.Length >= 4 && conteudo[0] == 0x50 && conteudo[1] == 0x4B)
            {
                return ".zip";
            }
            if (conteudo.Length >= 4 && conteudo[0] == 0x52 && conteudo[1] == 0x49 &&
                conteudo[2] == 0x46 && conteudo[3] == 0x46)
            {
                return ".wav";
            }
            return ".bin";
        }

        private static string Limpar(string texto)
        {
            if (String.IsNullOrEmpty(texto)) return "desconhecido";
            StringBuilder limpo = new StringBuilder(texto.Length);
            foreach (char c in texto)
            {
                bool proibido = c == ':' || c == '\\' || c == '/' || c == '*' ||
                    c == '?' || c == '"' || c == '<' || c == '>' || c == '|' ||
                    c < 32;
                limpo.Append(proibido ? '-' : c);
            }
            string resultado = limpo.ToString().Trim();
            return resultado.Length == 0 ? "desconhecido" : resultado;
        }
    }

    internal abstract class InboxRowBase : Panel
    {
        public long Seq { get; set; }
        public string OperationId { get; set; }
        public string Address { get; set; }

        private bool sent;
        private string rowTime = "";

        // Horário da linha (HH:mm): usado nos nomes sugeridos do "Salvar como".
        public string RowTime { get { return rowTime; } }

        // Data da mensagem: o Seq guarda os ticks; sem ele, hoje.
        protected DateTime DataDaSequencia()
        {
            try
            {
                if (Seq > 0) return new DateTime(Seq);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
            return DateTime.Now;
        }

        // Grade da linha: horário recebido | mensagem recebida | mensagem
        // enviada | horário enviado. O conteúdo da subclasse vive na célula da
        // sua direção (ContentCell); as células de horário se preenchem uma de
        // cada vez conforme a direção.
        protected readonly TableLayoutPanel Grid;
        protected readonly Panel CellReceived;
        protected readonly Panel CellSent;
        protected readonly Label CellTimeReceived;
        protected readonly Label CellTimeSent;

        protected InboxRowBase()
        {
            BackColor = Color.White;

            Grid = new TableLayoutPanel();
            Grid.ColumnCount = 4;
            Grid.RowCount = 1;
            // Sem Dock: os limites da grade são aplicados na mão a cada resize
            // (ver AjustarGrade). Com Dock=Fill a grade dependia do layout do
            // WinForms, que às vezes ficava pendente e deixava a grade com a
            // altura antiga — a "lacuna sem tabela" com o texto cortado.
            Grid.CellBorderStyle = TableLayoutPanelCellBorderStyle.Single;
            Grid.Margin = new Padding(0);
            Grid.Padding = new Padding(0);
            Grid.BackColor = Color.White;
            Grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40F));
            Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40F));

            CellTimeReceived = NovoRotuloDeHorario();
            CellReceived = NovaCelula();
            CellSent = NovaCelula();
            CellTimeSent = NovoRotuloDeHorario();

            Grid.Controls.Add(CellTimeReceived, 0, 0);
            Grid.Controls.Add(CellReceived, 1, 0);
            Grid.Controls.Add(CellSent, 2, 0);
            Grid.Controls.Add(CellTimeSent, 3, 0);

            Controls.Add(Grid);
            AjustarGrade();
            Resize += delegate { AjustarGrade(); LayoutRow(); };
            // As células só ganham tamanho real quando a grade faz o layout:
            // refazer o layout quando elas mudam mantém o conteúdo centrado.
            CellReceived.Resize += delegate { LayoutRow(); };
            CellSent.Resize += delegate { LayoutRow(); };
        }

        private static Label NovoRotuloDeHorario()
        {
            Label rotulo = new Label();
            rotulo.Dock = DockStyle.Fill;
            rotulo.TextAlign = ContentAlignment.MiddleCenter;
            rotulo.AutoSize = false;
            rotulo.Font = new Font("Segoe UI", 8.25F);
            rotulo.ForeColor = Color.FromArgb(75, 85, 99);
            rotulo.BackColor = Color.White;
            return rotulo;
        }

        // Fonte em negrito para o nome do remetente/destinatário: uma única
        // instância para todas as linhas (Font é um recurso GDI; uma por linha
        // vazaria handles com o histórico cheio).
        private static Font boldNameFont;
        private static string boldNameKey;

        protected static Font Bold(Font font)
        {
            string key = font.FontFamily.Name + "|" +
                font.SizeInPoints.ToString(CultureInfo.InvariantCulture);
            if (boldNameFont == null || boldNameKey != key)
            {
                boldNameFont = new Font(font, FontStyle.Bold);
                boldNameKey = key;
            }
            return boldNameFont;
        }

        // Mantém a grade colada no tamanho da linha. É o elo que o layout do
        // WinForms não garantia quando a janela mudava de largura: a grade
        // ficava com a altura do tamanho anterior, cortava o texto e deixava
        // a "lacuna sem tabela" até um novo resize. Aqui o ajuste é direto e
        // síncrono (SetBounds + PerformLayout), sem depender do layout adiado.
        public void AjustarGrade()
        {
            if (Grid == null) return;
            int largura = Math.Max(1, ClientSize.Width);
            int altura = Math.Max(1, ClientSize.Height);
            if (Grid.Left != 0 || Grid.Top != 0 ||
                Grid.Width != largura || Grid.Height != altura)
            {
                Grid.SetBounds(0, 0, largura, altura);
                Grid.PerformLayout();
            }
        }

        // Largura útil do conteúdo dentro da célula: as duas colunas de horário
        // (40 + 40) e as cinco bordas da grade (5 x 1) são fixas; cada coluna
        // do meio fica com metade do resto, menos a folga lateral (8). Sai da
        // própria linha de propósito: o ClientSize da célula fica com o valor
        // atrasado enquanto o layout da grade está pendente, e era isso que
        // deixava as linhas desalinhadas até maximizar/restaurar a janela.
        protected int CellContentWidth
        {
            get { return Math.Max(16, (Width - 101) / 2); }
        }

        // Mede o texto como o PRÓPRIO Label o desenha: GetPreferredSize usa as
        // mesmas flags do desenho, então quebra inclusive palavras longas sem
        // espaço — o TextRenderer+WordBreak devolvia essas em linha única
        // (teste: 494x17 medidos contra 174x72 desenhados) e o texto estourava
        // a célula. Medir na hora também evita depender do AutoSize, que fica
        // pendente quando o painel está com SuspendLayout.
        protected static Size MedirTexto(Label rotulo, int larguraMaxima)
        {
            if (rotulo == null) return new Size(4, 12);
            if (String.IsNullOrEmpty(rotulo.Text)) return new Size(4, rotulo.Font.Height);
            Size medido = rotulo.GetPreferredSize(
                new Size(Math.Max(16, larguraMaxima), int.MaxValue));
            return new Size(medido.Width + 2, Math.Max(rotulo.Font.Height, medido.Height));
        }

        private static Panel NovaCelula()
        {
            Panel celula = new Panel();
            celula.Dock = DockStyle.Fill;
            celula.Margin = new Padding(0);
            celula.BackColor = Color.White;
            return celula;
        }

        // Célula onde a subclasse coloca o seu conteúdo (direção atual).
        protected Panel ContentCell
        {
            get { return sent ? CellSent : CellReceived; }
        }

        public void SetRowTime(string time)
        {
            rowTime = time ?? "";
            ApplyCells();
        }

        // Cada lado aparece uma única vez: a recebida usa as duas primeiras
        // colunas; a enviada, as duas últimas.
        protected void ApplyCells()
        {
            CellTimeReceived.Text = sent ? "" : rowTime;
            CellTimeSent.Text = sent ? rowTime : "";
        }

        public bool Sent
        {
            get { return sent; }
            set
            {
                bool mudou = sent != value;
                sent = value;
                ApplyCells();
                if (mudou) ReposicionarConteudo();
                OnSentChanged();
            }
        }

        private void ReposicionarConteudo()
        {
            Panel origem = sent ? CellReceived : CellSent;
            Panel destino = sent ? CellSent : CellReceived;
            if (origem.Controls.Count == 0) return;
            Control[] mover = new Control[origem.Controls.Count];
            origem.Controls.CopyTo(mover, 0);
            origem.Controls.Clear();
            destino.Controls.AddRange(mover);
        }

        protected virtual void OnSentChanged()
        {
        }

        public virtual void LayoutRow()
        {
        }

        public string TranscriptionText { get; set; }

        // ---- Ações do menu do botão direito ---------------------------------
        // Cada linha responde pelo que o menu mostra (transcrição do áudio,
        // miniatura da imagem ou nome do arquivo) e pelas ações do menu.
        public virtual string MenuPrimaryText { get { return ""; } }
        public virtual Image MenuThumbnail { get { return null; } }
        public virtual void MenuPrimaryAction() { }
        // Copiar: imagem (linhas com imagem) ou texto (linhas de mensagem).
        public virtual bool CanCopy { get { return false; } }
        public virtual void CopyContent() { }
        public virtual bool CanSaveAs { get { return false; } }
        public virtual void SaveAs() { }
    }


    // Rótulo que desenha o texto com a SUA própria quebra de linha. O Label
    // do WinForms mede uma coisa e desenha outra quando o texto é uma palavra
    // longa sem espaços (medido: desenhava só ~2,5 linhas e escondia o resto).
    // Aqui medir e desenhar usam o mesmo algoritmo, então o que está na tela
    // é exatamente o que foi medido — com quebra por caractere quando a
    // palavra não cabe na largura.
    internal sealed class WrapLabel : Control
    {
        public WrapLabel()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint, true);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            List<string> linhas = Quebrar(Text ?? "", Font, Width);
            int altura = AlturaLinha(Font);
            int y = 0;
            foreach (string linha in linhas)
            {
                TextRenderer.DrawText(e.Graphics, linha, Font,
                    new Point(0, y), ForeColor,
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                y += altura;
            }
        }

        // Altura de uma linha de texto como o desenho a usa.
        public static int AlturaLinha(Font fonte)
        {
            return TextRenderer.MeasureText("Xg", fonte).Height + 1;
        }

        // Mesma quebra do desenho: por palavras e, se a palavra não couber,
        // por caractere. Devolve as linhas já prontas.
        public static List<string> Quebrar(string texto, Font fonte, int larguraMaxima)
        {
            List<string> linhas = new List<string>();
            if (String.IsNullOrEmpty(texto)) return linhas;
            int limite = Math.Max(16, larguraMaxima);
            string normalizado = texto.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (string paragrafo in normalizado.Split('\n'))
            {
                string atual = "";
                foreach (string palavra in paragrafo.Split(' '))
                {
                    string tentativa = atual.Length == 0 ? palavra : atual + " " + palavra;
                    if (TextRenderer.MeasureText(tentativa, fonte).Width <= limite)
                    {
                        atual = tentativa;
                        continue;
                    }
                    if (atual.Length > 0)
                    {
                        linhas.Add(atual);
                        atual = "";
                    }
                    // Palavra maior que a linha inteira: quebra por caractere.
                    string pedaco = "";
                    foreach (char c in palavra)
                    {
                        string tentativa2 = pedaco + c;
                        if (TextRenderer.MeasureText(tentativa2, fonte).Width <= limite)
                        {
                            pedaco = tentativa2;
                        }
                        else
                        {
                            linhas.Add(pedaco);
                            pedaco = c.ToString();
                        }
                    }
                    atual = pedaco;
                }
                linhas.Add(atual);
            }
            return linhas;
        }

        // Tamanho que o desenho precisa (mesmo algoritmo do OnPaint).
        public static Size Medir(string texto, Font fonte, int larguraMaxima)
        {
            if (String.IsNullOrEmpty(texto)) return new Size(4, fonte.Height);
            List<string> linhas = Quebrar(texto, fonte, larguraMaxima);
            int largura = 0;
            foreach (string linha in linhas)
            {
                int l = TextRenderer.MeasureText(linha, fonte).Width;
                if (l > largura) largura = l;
            }
            int altura = Math.Max(AlturaLinha(fonte), linhas.Count * AlturaLinha(fonte));
            return new Size(
                Math.Min(largura + 2, Math.Max(16, larguraMaxima) + 2), altura);
        }
    }


    // Linha de mensagem de texto: a célula da direção mostra o nome em
    // negrito e o conteúdo centralizado — sem o horário, que vive na coluna
    // própria da tabela.
    internal sealed class InboxTextRow : InboxRowBase
    {
        private readonly Label nameLabel;
        private readonly WrapLabel messageLabel;
        private readonly string whoText;
        private readonly string bodyText;

        private bool layingOutRow;
        private string lastSig = "";

        public InboxTextRow(string who, string text, string time, bool sent, Font font)
        {
            whoText = who ?? "";
            bodyText = text ?? "";

            nameLabel = new Label();
            nameLabel.Font = Bold(font);
            nameLabel.AutoSize = false;
            nameLabel.TextAlign = ContentAlignment.MiddleLeft;
            nameLabel.UseMnemonic = false;

            messageLabel = new WrapLabel();
            messageLabel.Font = font;

            SetRowTime(time);
            MontarConteudo();
            Sent = sent;
        }

        // Nome em cima (negrito, centralizado) e a mensagem embaixo.
        private void MontarConteudo()
        {
            Panel celula = ContentCell;
            celula.Controls.Clear();
            nameLabel.Text = whoText;
            messageLabel.Text = bodyText;
            Color cor = Sent ? InboxPanel.SentColor : Color.FromArgb(31, 41, 55);
            nameLabel.ForeColor = cor;
            messageLabel.ForeColor = cor;
            celula.Controls.Add(nameLabel);
            celula.Controls.Add(messageLabel);
            lastSig = "";
            LayoutRow();
        }

        protected override void OnSentChanged()
        {
            MontarConteudo();
        }

        public override void LayoutRow()
        {
            if (layingOutRow) return;
            // Largura útil calculada da própria linha (fórmula da grade), não
            // do ClientSize da célula, que pode estar defasado.
            int available = CellContentWidth;
            string sig = whoText + "|" + bodyText + "|" + (Sent ? "1" : "0") + "|" +
                available;
            if (sig == lastSig) return;

            layingOutRow = true;
            try
            {
                // Nome em cima, mensagem embaixo, tudo centralizado:
                //     LUIZ
                //     Bom dia, Gustavo!
                Size tamanhoNome = MedirTexto(nameLabel, available);
                Size tamanhoCorpo = WrapLabel.Medir(
                    messageLabel.Text, messageLabel.Font, available);
                nameLabel.Size = tamanhoNome;
                messageLabel.Size = tamanhoCorpo;

                int folga = 2;
                int alturaBloco = tamanhoNome.Height + folga + tamanhoCorpo.Height;
                int topo = 4;
                nameLabel.Location = new Point(
                    Math.Max(1, (available - tamanhoNome.Width) / 2), topo);
                messageLabel.Location = new Point(
                    Math.Max(1, (available - tamanhoCorpo.Width) / 2),
                    topo + tamanhoNome.Height + folga);

                int altura = Math.Max(18, alturaBloco + 8);
                if (Height != altura) Height = altura;
                lastSig = sig;
            }
            finally
            {
                layingOutRow = false;
            }
        }

        public new string Text
        {
            get { return nameLabel.Text + messageLabel.Text; }
        }

        // Copiar (menu): o texto da mensagem vai para a área de transferência.
        public override bool CanCopy
        {
            get { return messageLabel != null && !String.IsNullOrEmpty(messageLabel.Text); }
        }

        public override void CopyContent()
        {
            string texto = messageLabel == null ? "" : messageLabel.Text;
            if (String.IsNullOrEmpty(texto)) return;
            try
            {
                Clipboard.SetText(texto);
            }
            catch (Exception)
            {
                // Sem área de transferência disponível: nada a fazer.
            }
        }
    }


    internal sealed class InboxImageRow : InboxRowBase
    {
        private static Image imageGlyph;
        private readonly byte[] imageBytes;
        private readonly string filePath;
        // Linha de arquivo: ícone do clipe e clique = salvar como.
        private readonly bool asFile;
        private readonly string fileName;
        // Pacote com vários arquivos (zip transparente): quantidade e nomes
        // originais separados por "\n"; 0/1 = arquivo único.
        private readonly int fileCount;
        private readonly string fileNames;
        private readonly string whoText;
        private readonly string bodyText;
        private readonly Label whoLabel;
        private readonly Label bodyLabel;
        private readonly IconButton openButton;
        // Selo "xN" do pacote: Label pequena à direita do ícone (fora dele),
        // em vez de desenhar sobre o clipe.
        private readonly Label badgeLabel;
        private Image menuThumbnail;

        private bool layingOutRow;
        private string lastSig = "";

        public InboxImageRow(
            string who,
            string body,
            byte[] imageBytes,
            string filePath,
            int iconSize,
            Font font)
            : this(who, body, imageBytes, filePath, iconSize, font, false, "")
        {
        }

        public InboxImageRow(
            string who,
            string body,
            byte[] imageBytes,
            string filePath,
            int iconSize,
            Font font,
            bool asFile,
            string fileName,
            int fileCount = 1,
            string fileNames = "")
        {
            this.imageBytes = imageBytes;
            this.filePath = filePath;
            this.asFile = asFile;
            this.fileName = fileName;
            this.fileCount = fileCount <= 0 ? 1 : fileCount;
            this.fileNames = fileNames ?? "";
            whoText = who ?? "";
            bodyText = body ?? "";
            BackColor = Color.White;

            whoLabel = new Label();
            whoLabel.Font = Bold(font);
            whoLabel.AutoSize = false;
            whoLabel.TextAlign = ContentAlignment.MiddleLeft;
            whoLabel.UseMnemonic = false;
            ContentCell.Controls.Add(whoLabel);

            bodyLabel = new Label();
            bodyLabel.Font = font;
            bodyLabel.AutoSize = false;
            bodyLabel.TextAlign = ContentAlignment.MiddleLeft;
            bodyLabel.UseMnemonic = false;
            ContentCell.Controls.Add(bodyLabel);

            openButton = new IconButton();
            openButton.Glyph = IconGlyph.None;
            openButton.SourceImage = asFile
                ? AppResources.AudioIconClip()
                : LoadImageGlyph();
            openButton.CircleColor = Color.White;
            openButton.CircleOutline = Color.FromArgb(196, 202, 210);
            openButton.Size = new Size(iconSize, iconSize);
            openButton.Location = new Point(0, 0);
            openButton.Enabled = HasImage();
            openButton.AccessibleName = asFile ? "Abrir arquivo" : "Abrir imagem";
            // Clique no ícone abre no aplicativo padrão; salvar continua no
            // menu do botão direito ("Salvar como").
            openButton.Click += delegate { OpenImage(); };
            ContentCell.Controls.Add(openButton);

            // Selo "xN" fora do ícone (à direita, embaixo), menor que o botão:
            // igual ao selo da prévia antes de enviar.
            badgeLabel = new Label();
            badgeLabel.AutoSize = true;
            badgeLabel.Font = new Font(font.FontFamily, 7F, FontStyle.Bold);
            badgeLabel.Padding = new Padding(3, 1, 3, 1);
            badgeLabel.BackColor = Color.FromArgb(31, 41, 55);
            badgeLabel.ForeColor = Color.White;
            badgeLabel.Text = "x" + fileCount.ToString(CultureInfo.InvariantCulture);
            badgeLabel.Visible = fileCount > 1;
            ContentCell.Controls.Add(badgeLabel);

            MontarConteudo();
        }

        protected override void OnSentChanged()
        {
            MontarConteudo();
        }

        // Nome em cima; embaixo o ícone (imagem ou clipe) com o tamanho:
        //     MIRELLA
        //     [ícone] (50 kb)
        private void MontarConteudo()
        {
            Panel celula = ContentCell;
            celula.Controls.Clear();
            whoLabel.Text = whoText;
            bodyLabel.Text = bodyText;
            Color cor = Sent ? InboxPanel.SentColor : Color.FromArgb(31, 41, 55);
            whoLabel.ForeColor = cor;
            bodyLabel.ForeColor = cor;
            celula.Controls.Add(whoLabel);
            celula.Controls.Add(bodyLabel);
            celula.Controls.Add(openButton);
            celula.Controls.Add(badgeLabel);
            badgeLabel.Visible = fileCount > 1;
            lastSig = "";
            LayoutRow();
        }

        public override void LayoutRow()
        {
            if (layingOutRow) return;
            // Largura útil calculada da própria linha (fórmula da grade), não
            // do ClientSize da célula, que pode estar defasado.
            int available = CellContentWidth;
            string sig = whoText + "|" + bodyText + "|" + (Sent ? "1" : "0") + "|" +
                available + "|" + openButton.Width + "|" + fileCount;
            if (sig == lastSig) return;

            layingOutRow = true;
            try
            {
                // Nome em cima; embaixo o ícone, o selo "xN" (pacote) e o
                // tamanho, tudo centralizado:
                //     MIRELLA
                //     [ícone][x3] (3 arquivos, 50 kb)
                int seloW = 0;
                int seloH = 0;
                if (fileCount > 1)
                {
                    Size seloPref = badgeLabel.GetPreferredSize(Size.Empty);
                    seloW = seloPref.Width;
                    seloH = seloPref.Height;
                }
                Size tamanhoNome = MedirTexto(whoLabel, available);
                Size tamanhoCorpo = MedirTexto(bodyLabel,
                    Math.Max(60, available - openButton.Width - seloW - 16));
                whoLabel.Size = tamanhoNome;
                bodyLabel.Size = tamanhoCorpo;

                int gap = 4;
                int folga = 2;
                int espacoSelo = fileCount > 1 ? 2 : 0;
                int larguraLinha2 = openButton.Width + espacoSelo + seloW +
                    gap + tamanhoCorpo.Width;
                int alturaLinha2 = Math.Max(openButton.Height, tamanhoCorpo.Height);
                int alturaBloco = tamanhoNome.Height + folga + alturaLinha2;
                int topo = 4;

                whoLabel.Location = new Point(
                    Math.Max(1, (available - tamanhoNome.Width) / 2), topo);
                int left2 = Math.Max(1, (available - larguraLinha2) / 2);
                int topo2 = topo + tamanhoNome.Height + folga;
                openButton.Location = new Point(left2,
                    topo2 + Math.Max(0, (alturaLinha2 - openButton.Height) / 2));
                if (fileCount > 1)
                {
                    // Selo fora do ícone: à direita dele, encostado embaixo.
                    badgeLabel.Location = new Point(
                        left2 + openButton.Width + espacoSelo,
                        topo2 + alturaLinha2 - seloH);
                    bodyLabel.Location = new Point(
                        left2 + openButton.Width + espacoSelo + seloW + gap,
                        topo2 + Math.Max(0, (alturaLinha2 - tamanhoCorpo.Height) / 2));
                }
                else
                {
                    bodyLabel.Location = new Point(left2 + openButton.Width + gap,
                        topo2 + Math.Max(0, (alturaLinha2 - tamanhoCorpo.Height) / 2));
                }

                int altura = Math.Max(openButton.Height + 6, alturaBloco + 8);
                if (Height != altura) Height = altura;
                lastSig = sig;
            }
            finally
            {
                layingOutRow = false;
            }
        }

        // O ícone vem embutido no executável (assets/imagem.png).
        private static Image LoadImageGlyph()
        {
            if (imageGlyph == null)
            {
                try
                {
                    imageGlyph = AppResources.AudioIconImage();
                }
                catch
                {
                    imageGlyph = null;
                }
            }
            return imageGlyph;
        }

        // Grava os bytes em um arquivo temporário e entrega ao sistema, que
        // abre com o aplicativo padrão do usuário (mesmo caminho do Explorer).
        private bool HasImage()
        {
            return (imageBytes != null && imageBytes.Length > 0) ||
                (!String.IsNullOrEmpty(filePath) && File.Exists(filePath));
        }

        // Arquivo recebido: o clique abre o "salvar como".
        private void SaveFileAs()
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                MessageBox.Show(this, "Este arquivo não está mais disponível.",
                    "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Salvar arquivo";
                dialog.FileName = String.IsNullOrEmpty(fileName)
                    ? NomeDeMidia.Gerar(
                        "arquivo",
                        Sent,
                        whoText,
                        RowTime,
                        DataDaSequencia(),
                        ExtensaoAparente())
                    : fileName;
                dialog.Filter = "Todos os arquivos|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllBytes(dialog.FileName, imageBytes);
                    MessageBox.Show(this, "Arquivo salvo em " + dialog.FileName,
                        "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, "Não foi possível salvar: " + error.Message,
                        "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        // Pacote com vários arquivos: escolhe a pasta e descompacta tudo lá
        // (por trás é um zip; o usuário só vê o clipe com "xN").
        private void SaveBundleAs()
        {
            byte[] zipBytes = imageBytes;
            if ((zipBytes == null || zipBytes.Length == 0) &&
                !String.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    zipBytes = File.ReadAllBytes(filePath);
                }
                catch
                {
                    zipBytes = null;
                }
            }
            if (zipBytes == null || zipBytes.Length == 0)
            {
                MessageBox.Show(this, "Este arquivo não está mais disponível.",
                    "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<FileZipBundle.ZipEntry> entries;
            if (!FileZipBundle.TryRead(zipBytes, out entries) || entries.Count == 0)
            {
                MessageBox.Show(this, "Não foi possível abrir o pacote de arquivos.",
                    "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Escolher a pasta para salvar os " +
                    entries.Count + " arquivos";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                int saved;
                string error;
                if (!FileZipBundle.TryExtractToFolder(
                    entries, dialog.SelectedPath, out saved, out error))
                {
                    MessageBox.Show(this, "Não foi possível salvar: " + error,
                        "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                MessageBox.Show(this, saved + " arquivos salvos em " + dialog.SelectedPath,
                    "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // Confere o começo do arquivo: devolve null quando já é PNG (abre
        // como está) e a extensão certa quando é outro tipo conhecido.
        private static string DetectarExtensaoForaDoPng(string caminho)
        {
            try
            {
                byte[] inicio = new byte[8];
                int lidos;
                using (System.IO.FileStream stream = System.IO.File.OpenRead(caminho))
                {
                    lidos = stream.Read(inicio, 0, inicio.Length);
                }
                if (lidos >= 8 && inicio[0] == 0x89 && inicio[1] == 0x50 &&
                    inicio[2] == 0x4E && inicio[3] == 0x47)
                {
                    return null;   // já é PNG
                }
                if (lidos >= 4 && inicio[0] == 0x25 && inicio[1] == 0x50 &&
                    inicio[2] == 0x44 && inicio[3] == 0x46)
                {
                    return ".pdf";
                }
                if (lidos >= 2 && inicio[0] == 0xFF && inicio[1] == 0xD8)
                {
                    return ".jpg";
                }
                if (lidos >= 4 && inicio[0] == 0x47 && inicio[1] == 0x49 &&
                    inicio[2] == 0x46)
                {
                    return ".gif";
                }
                if (lidos >= 2 && inicio[0] == 0x42 && inicio[1] == 0x4D)
                {
                    return ".bmp";
                }
                if (lidos >= 4 && inicio[0] == 0x50 && inicio[1] == 0x4B)
                {
                    return ".zip";
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        // Miniatura quadrada igual à prévia do anexo (PictureBox com Zoom):
        // a imagem é ajustada ao quadrado e centralizada, ampliando as
        // pequenas (a prévia do anexo também amplia).
        private static Image CriarMiniaturaDaPrevia(byte[] pngBytes, int lado)
        {
            try
            {
                using (MemoryStream buffer = new MemoryStream(pngBytes))
                using (Image original = Image.FromStream(buffer))
                {
                    Bitmap quadrado = new Bitmap(lado, lado);
                    using (Graphics desenho = Graphics.FromImage(quadrado))
                    {
                        desenho.Clear(Color.White);
                        desenho.InterpolationMode =
                            System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        double escala = Math.Min(
                            (double)lado / original.Width,
                            (double)lado / original.Height);
                        int largura = Math.Max(1, (int)Math.Round(original.Width * escala));
                        int altura = Math.Max(1, (int)Math.Round(original.Height * escala));
                        desenho.DrawImage(original,
                            (lado - largura) / 2, (lado - altura) / 2, largura, altura);
                    }
                    return quadrado;
                }
            }
            catch
            {
                return null;
            }
        }

        // ---- Ações do menu do botão direito (imagem/arquivo) ---------------

        public override string MenuPrimaryText
        {
            get
            {
                if (!asFile) return "";
                if (fileCount > 1) return DescribeBundle();
                return String.IsNullOrEmpty(fileName)
                    ? Path.GetFileName(filePath)
                    : fileName;
            }
        }

        // "3 arquivos: a.pdf, b.doc, ..." (curto, para o menu do botão direito).
        private string DescribeBundle()
        {
            string list = String.Join(", ", SplitBundleNames());
            if (list.Length > 80) list = list.Substring(0, 77) + "...";
            return fileCount + " arquivos: " + list;
        }

        private string[] SplitBundleNames()
        {
            if (String.IsNullOrEmpty(fileNames)) return new string[0];
            return fileNames.Split(new char[] { '\n' });
        }

        public override Image MenuThumbnail
        {
            get
            {
                if (asFile || imageBytes == null || imageBytes.Length == 0) return null;
                if (menuThumbnail == null)
                {
                    // Mesmo porte (56x56) da prévia usada ao anexar para
                    // enviar, com a imagem ajustada e centralizada no quadrado.
                    menuThumbnail = CriarMiniaturaDaPrevia(imageBytes, 56);
                }
                return menuThumbnail;
            }
        }

        // Clique no item principal (miniatura ou nome do arquivo): abre no
        // aplicativo padrão do sistema.
        public override void MenuPrimaryAction()
        {
            if (asFile && fileCount > 1)
            {
                // Pacote: o clique abre o "salvar como" (pasta).
                SaveAs();
                return;
            }
            OpenImage();
        }

        public override bool CanCopy
        {
            get { return !asFile && imageBytes != null && imageBytes.Length > 0; }
        }

        public override void CopyContent()
        {
            string error;
            if (ImageTransfer.TryCopyToClipboard(imageBytes, out error)) return;
            MessageBox.Show(this, "Não foi possível copiar a imagem: " + error,
                "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public override bool CanSaveAs
        {
            get { return imageBytes != null && imageBytes.Length > 0; }
        }

        public override void SaveAs()
        {
            if (asFile)
            {
                if (fileCount > 1)
                {
                    // Pacote: descompacta tudo na pasta que o usuário escolher.
                    SaveBundleAs();
                    return;
                }
                // Arquivo: o diálogo já sugere o nome original.
                SaveFileAs();
                return;
            }
            if (imageBytes == null || imageBytes.Length == 0) return;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Salvar como";
                dialog.FileName = NomeDeMidia.Gerar(
                    "imagem",
                    Sent,
                    whoText,
                    RowTime,
                    DataDaSequencia(),
                    ".png");
                dialog.Filter = "Imagem PNG|*.png|Todos os arquivos|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllBytes(dialog.FileName, imageBytes);
                    MessageBox.Show(this, "Imagem salva em " + dialog.FileName,
                        "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, "Não foi possível salvar: " + error.Message,
                        "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        // Extensão aparente do conteúdo salvo (registros antigos sem nome).
        private string ExtensaoAparente()
        {
            try
            {
                if (!String.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    string extensao = DetectarExtensaoForaDoPng(filePath);
                    if (extensao != null) return extensao;
                }
            }
            catch
            {
            }
            return String.IsNullOrEmpty(fileName) ? ".bin" : Path.GetExtension(fileName);
        }

        private void OpenImage()
        {
            // Imagem do histórico persistente: o arquivo já está em disco.
            if (!String.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    // Arquivos antigos salvos com a extensão errada (por
                    // exemplo um PDF com nome ".png") abriam no visualizador
                    // de imagem e acusavam "corrompida": confere o conteúdo e,
                    // quando não for PNG, abre uma cópia com a extensão certa
                    // para o sistema escolher o aplicativo adequado.
                    string alvo = filePath;
                    string extensao = DetectarExtensaoForaDoPng(filePath);
                    if (extensao != null)
                    {
                        string directory = Path.Combine(Path.GetTempPath(), "TailMsg");
                        Directory.CreateDirectory(directory);
                        string copia = Path.Combine(
                            directory,
                            "midia-" + Path.GetFileNameWithoutExtension(filePath) + extensao);
                        if (!File.Exists(copia))
                        {
                            File.Copy(filePath, copia);
                        }
                        alvo = copia;
                    }
                    Process.Start(new ProcessStartInfo(alvo) { UseShellExecute = true });
                }
                catch (Exception error)
                {
                    MessageBox.Show(
                        this,
                        "Não foi possível abrir a imagem: " + error.Message,
                        "TailMsg",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return;
            }

            if (imageBytes == null || imageBytes.Length == 0)
            {
                MessageBox.Show(
                    this,
                    "Esta imagem não está mais disponível.",
                    "TailMsg",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                string directory = Path.Combine(Path.GetTempPath(), "TailMsg");
                Directory.CreateDirectory(directory);
                string fileName = asFile
                    ? "midia-" + ShortHash(imageBytes) + "-" +
                        (String.IsNullOrEmpty(this.fileName)
                            ? "arquivo"
                            : Path.GetFileName(this.fileName))
                    : "imagem-" + ShortHash(imageBytes) + ".png";
                string fullPath = Path.Combine(directory, fileName);
                if (!File.Exists(fullPath))
                {
                    File.WriteAllBytes(fullPath, imageBytes);
                }
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(
                    this,
                    "Não foi possível abrir a imagem: " + error.Message,
                    "TailMsg",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private static string ShortHash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder text = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return text.ToString();
            }
        }
    }


    // Uma linha de áudio do histórico: prefixo, play/pause e transcrição.
    internal sealed class InboxAudioRow : InboxRowBase
    {
        private readonly AudioPayload audio;
        private readonly IconButton playButton;
        private readonly Label whoLabel;
        private readonly Label bodyLabel;
        private readonly Label transcriptionLabel;
        private readonly string whoText;
        private readonly string bodyText;
        private readonly WavePlayer player = new WavePlayer();
        private readonly System.Windows.Forms.Timer ticker;

        private bool layingOutRow;
        private string lastSig = "";

        public event EventHandler PlayRequested;

        // Identificador da linha no histórico persistente (0 quando não gravada).
        public long HistorySeq { get; set; }

        // O layout da linha precisa rodar de novo quando o estado "enviada" é
        // definido depois da criação (o Append* já desenha a linha).
        public void RefreshLayout()
        {
            LayoutRow();
        }

        // O payload fica exposto para a transcrição do histórico.
        public AudioPayload Audio
        {
            get { return audio; }
        }

        public bool IsPlaying
        {
            get { return player.IsPlaying; }
        }

        public InboxAudioRow(
            string who,
            string body,
            AudioPayload payload,
            string operationId,
            int playerSize,
            Font font)
        {
            audio = payload;
            OperationId = operationId;
            BackColor = Color.White;
            whoText = who ?? "";
            bodyText = body ?? "";

            string loadError = "";
            bool loaded = payload != null && payload.WavBytes != null &&
                player.Load(payload.WavBytes, out loadError);

            whoLabel = new Label();
            whoLabel.Font = Bold(font);
            whoLabel.AutoSize = false;
            whoLabel.TextAlign = ContentAlignment.MiddleLeft;
            whoLabel.UseMnemonic = false;
            ContentCell.Controls.Add(whoLabel);

            bodyLabel = new Label();
            bodyLabel.Font = font;
            bodyLabel.AutoSize = false;
            bodyLabel.TextAlign = ContentAlignment.MiddleLeft;
            bodyLabel.UseMnemonic = false;
            ContentCell.Controls.Add(bodyLabel);

            playButton = new IconButton();
            playButton.CircleColor = Color.FromArgb(242, 207, 55);
            playButton.CircleOutline = Color.FromArgb(196, 157, 0);
            playButton.Glyph = IconGlyph.Play;
            playButton.Size = new Size(playerSize, playerSize);
            playButton.Location = new Point(0, 0);
            playButton.Enabled = loaded;
            playButton.AccessibleName = "Reproduzir áudio";
            playButton.Click += delegate { Toggle(); };
            ContentCell.Controls.Add(playButton);

            transcriptionLabel = new Label();
            transcriptionLabel.AutoSize = false;
            transcriptionLabel.Font = font;
            transcriptionLabel.ForeColor = Color.FromArgb(75, 85, 99);
            transcriptionLabel.Text = "";
            transcriptionLabel.Size = new Size(1, 1);
            ContentCell.Controls.Add(transcriptionLabel);

            MontarConteudo();

            ticker = new System.Windows.Forms.Timer();
            ticker.Interval = 150;
            ticker.Tick += delegate { Tick(); };
            // NÃO inicia aqui: um timer ligado por linha de áudio deixava o app
            // cada vez mais lento conforme o histórico cresce (medido: 6,5% de
            // um núcleo parado com 16 áudios). O timer só roda enquanto este
            // áudio está tocando.
        }

        // Texto da transcrição, exibido depois do botão de play.
        // A transcrição não aparece no histórico: fica guardada para o menu de
        // contexto (item entre aspas, em itálico).
        public string Transcription { get; private set; }

        public void SetTranscription(string text)
        {
            Transcription = text ?? "";
            // O menu de contexto lê TranscriptionText (da base): mantém os dois
            // em sincronia para a transcrição que chega depois do envio.
            TranscriptionText = Transcription;
        }

        // O menu mostra a transcrição; o clique copia o texto.
        public override string MenuPrimaryText
        {
            get { return Transcription; }
        }

        public override void MenuPrimaryAction()
        {
            string texto = Transcription;
            if (String.IsNullOrEmpty(texto)) return;
            try
            {
                Clipboard.SetText(texto);
            }
            catch (Exception)
            {
                // Sem área de transferência disponível: nada a fazer.
            }
        }

        public override bool CanSaveAs
        {
            get { return audio != null && audio.WavBytes != null && audio.WavBytes.Length > 0; }
        }

        public override void SaveAs()
        {
            byte[] bytes = audio == null ? null : audio.WavBytes;
            if (bytes == null || bytes.Length == 0) return;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Salvar áudio";
                dialog.FileName = NomeDeMidia.Gerar(
                    "audio",
                    Sent,
                    whoText,
                    RowTime,
                    DataDaSequencia(),
                    ".wav");
                dialog.Filter = "Áudio WAV|*.wav|Todos os arquivos|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllBytes(dialog.FileName, bytes);
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, "Não foi possível salvar: " + error.Message,
                        "TailMsg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        // Nome em cima; embaixo o play e a duração:
        //     DELEGADO
        //     [play] (13s)
        private void MontarConteudo()
        {
            Panel celula = ContentCell;
            celula.Controls.Clear();
            whoLabel.Text = whoText;
            bodyLabel.Text = bodyText;
            Color cor = Sent ? InboxPanel.SentColor : Color.FromArgb(31, 41, 55);
            whoLabel.ForeColor = cor;
            bodyLabel.ForeColor = cor;
            celula.Controls.Add(whoLabel);
            celula.Controls.Add(bodyLabel);
            celula.Controls.Add(playButton);
            celula.Controls.Add(transcriptionLabel);
            transcriptionLabel.Location = new Point(0, 0);
            lastSig = "";
            LayoutRow();
        }

        protected override void OnSentChanged()
        {
            MontarConteudo();
        }

        public override void LayoutRow()
        {
            if (layingOutRow) return;
            // Largura útil calculada da própria linha (fórmula da grade), não
            // do ClientSize da célula, que pode estar defasado.
            int available = CellContentWidth;
            string sig = whoText + "|" + bodyText + "|" + (Sent ? "1" : "0") + "|" +
                available + "|" + playButton.Width;
            if (sig == lastSig) return;

            layingOutRow = true;
            try
            {
                // Nome em cima; embaixo o play e a duração, tudo centralizado:
                //     DELEGADO
                //     [play] (13s)
                Size tamanhoNome = MedirTexto(whoLabel, available);
                Size tamanhoCorpo = MedirTexto(bodyLabel,
                    Math.Max(60, available - playButton.Width - 12));
                whoLabel.Size = tamanhoNome;
                bodyLabel.Size = tamanhoCorpo;

                int gap = 4;
                int folga = 2;
                int larguraLinha2 = playButton.Width + gap + tamanhoCorpo.Width;
                int alturaLinha2 = Math.Max(playButton.Height, tamanhoCorpo.Height);
                int alturaBloco = tamanhoNome.Height + folga + alturaLinha2;
                int topo = 4;

                whoLabel.Location = new Point(
                    Math.Max(1, (available - tamanhoNome.Width) / 2), topo);
                int left2 = Math.Max(1, (available - larguraLinha2) / 2);
                int topo2 = topo + tamanhoNome.Height + folga;
                playButton.Location = new Point(left2,
                    topo2 + Math.Max(0, (alturaLinha2 - playButton.Height) / 2));
                bodyLabel.Location = new Point(left2 + playButton.Width + gap,
                    topo2 + Math.Max(0, (alturaLinha2 - tamanhoCorpo.Height) / 2));

                int altura = Math.Max(playButton.Height + 6, alturaBloco + 8);
                if (Height != altura) Height = altura;
                lastSig = sig;
            }
            finally
            {
                layingOutRow = false;
            }
        }

        public void StopPlayback()
        {
            player.Stop();
            if (ticker != null) ticker.Stop();
            UpdateGlyph();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (ticker != null)
                {
                    ticker.Stop();
                    ticker.Dispose();
                }
                player.Dispose();
            }
            base.Dispose(disposing);
        }

        private void Toggle()
        {
            EventHandler handler = PlayRequested;
            if (handler != null) handler(this, EventArgs.Empty);
            string error;
            if (player.IsPlaying)
            {
                player.Pause(out error);
            }
            else if (player.IsPaused)
            {
                player.Resume(out error);
            }
            else
            {
                player.Restart();
                player.Play(out error);
                if (player.IsPlaying && ticker != null) ticker.Start();
            }
            if (!player.IsPlaying && ticker != null) ticker.Stop();
            UpdateGlyph();
            LayoutRow();
        }

        private void Tick()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (!player.IsPlaying) return;
            UpdateGlyph();
        }

        private void UpdateGlyph()
        {
            IconGlyph expected = player.IsPlaying ? IconGlyph.Pause : IconGlyph.Play;
            if (playButton.Glyph == expected) return;
            playButton.Glyph = expected;
            playButton.AccessibleName = player.IsPlaying ? "Pausar áudio" : "Reproduzir áudio";
            playButton.Invalidate();
        }
    }


    // Endereço do serviço de transcrição. O padrão é o host `servidor` (o
    // mesmo usado pelo SIG); `localhost` não é usado porque o serviço escuta
    // apenas no IPv6 do host. O registro do usuário pode apontar outro
    // endereço em HKCU\Software\TailMsg\TranscriptionEndpoint.
    internal static class TranscriptionSettings
    {
        private const string SettingsKey = @"Software\TailMsg";
        private const string EndpointValueName = "TranscriptionEndpoint";
        private const string PrimaryEndpoint = "http://servidor:8100/transcribe";
        private const string FallbackEndpoint = "http://servidor.local:8100/transcribe";

        public static string ConfiguredEndpoint()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    if (key == null) return "";
                    string value = key.GetValue(EndpointValueName) as string;
                    return String.IsNullOrEmpty(value) ? "" : value.Trim();
                }
            }
            catch
            {
                return "";
            }
        }

        public static string[] Candidates()
        {
            List<string> endpoints = new List<string>();
            string configured = ConfiguredEndpoint();
            if (configured.Length > 0) endpoints.Add(configured);
            if (!ContainsIgnoreCase(endpoints, PrimaryEndpoint)) endpoints.Add(PrimaryEndpoint);
            if (!ContainsIgnoreCase(endpoints, FallbackEndpoint)) endpoints.Add(FallbackEndpoint);
            return endpoints.ToArray();
        }

        private static bool ContainsIgnoreCase(List<string> values, string candidate)
        {
            foreach (string value in values)
            {
                if (String.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    // Leitura do texto transcrito na resposta JSON, sem dependências externas.
    internal static class TranscriptionJson
    {
        private static readonly string[] Keys = new string[]
        {
            "text", "transcription", "transcript"
        };

        public static bool TryExtractText(string json, out string text)
        {
            text = "";
            if (String.IsNullOrEmpty(json)) return false;
            foreach (string key in Keys)
            {
                string value;
                if (TryFindStringValue(json, key, out value))
                {
                    string trimmed = value.Trim();
                    if (trimmed.Length > 0)
                    {
                        text = trimmed;
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool TryFindStringValue(string json, string key, out string value)
        {
            value = "";
            string pattern = "\"" + key + "\"";
            int searchFrom = 0;
            while (searchFrom < json.Length)
            {
                int keyIndex = json.IndexOf(pattern, searchFrom, StringComparison.Ordinal);
                if (keyIndex < 0) return false;
                searchFrom = keyIndex + pattern.Length;

                int colon = searchFrom;
                while (colon < json.Length && Char.IsWhiteSpace(json[colon])) colon++;
                if (colon >= json.Length || json[colon] != ':') continue;
                colon++;
                while (colon < json.Length && Char.IsWhiteSpace(json[colon])) colon++;
                // Valor que não é string (objeto/lista/número): ignora.
                if (colon >= json.Length || json[colon] != '"') continue;

                StringBuilder builder = new StringBuilder();
                int cursor = colon + 1;
                while (cursor < json.Length)
                {
                    char current = json[cursor];
                    if (current == '"') break;
                    if (current == '\\' && cursor + 1 < json.Length)
                    {
                        char escape = json[cursor + 1];
                        switch (escape)
                        {
                            case 'n': builder.Append('\n'); cursor += 2; break;
                            case 'r': builder.Append('\r'); cursor += 2; break;
                            case 't': builder.Append('\t'); cursor += 2; break;
                            case 'b': builder.Append('\b'); cursor += 2; break;
                            case 'f': builder.Append('\f'); cursor += 2; break;
                            case 'u':
                            {
                                int code;
                                if (cursor + 5 < json.Length &&
                                    Int32.TryParse(
                                        json.Substring(cursor + 2, 4),
                                        NumberStyles.HexNumber,
                                        CultureInfo.InvariantCulture,
                                        out code))
                                {
                                    builder.Append((char)code);
                                    cursor += 6;
                                }
                                else
                                {
                                    cursor += 2;
                                }
                                break;
                            }
                            default:
                                builder.Append(escape);
                                cursor += 2;
                                break;
                        }
                        continue;
                    }
                    builder.Append(current);
                    cursor++;
                }

                value = builder.ToString();
                return true;
            }
            return false;
        }
    }

    // Envia o WAV por multipart/form-data igual ao SIG (campo "files",
    // tipo audio/wav) e devolve o texto transcrito.
    internal static class TranscriptionClient
    {
        // A transcrição é informativa: se o servidor não responder rápido, o
        // app segue a vida (o áudio já foi entregue).
        private const int TimeoutMilliseconds = 30000;
        private const int AttemptsPerEndpoint = 2;

        public static bool TryTranscribe(
            byte[] wavBytes,
            string fileName,
            string operationId,
            out string text,
            out string error)
        {
            text = "";
            error = "";
            if (wavBytes == null || wavBytes.Length == 0)
            {
                error = "Não há áudio para transcrever.";
                return false;
            }
            if (String.IsNullOrEmpty(fileName))
            {
                fileName = "audio.wav";
            }

            DateTime started = DateTime.UtcNow;
            string lastError = "";
            string[] endpoints = TranscriptionSettings.Candidates();
            foreach (string endpoint in endpoints)
            {
                // O host `servidor` responde por IPv6 link-local e pode falhar
                // de forma intermitente: vale uma segunda tentativa.
                for (int attempt = 0; attempt < AttemptsPerEndpoint; attempt++)
                {
                    string failure;
                    string result;
                    if (TryPostOnce(endpoint, wavBytes, fileName, out result, out failure))
                    {
                        text = result;
                        TailMsgDiagnostics.WriteMessageEvent(
                            operationId,
                            "transcription",
                            "success",
                            "",
                            endpoint,
                            "",
                            Elapsed(started),
                            "chars=" + result.Length);
                        return true;
                    }
                    lastError = failure;
                }
            }

            error = lastError.Length > 0
                ? lastError
                : "O serviço de transcrição não respondeu.";
            TailMsgDiagnostics.WriteMessageEvent(
                operationId,
                "transcription",
                "failed",
                "",
                endpoints.Length > 0 ? endpoints[0] : "",
                "",
                Elapsed(started),
                lastError);
            return false;
        }

        private static long Elapsed(DateTime started)
        {
            return (long)(DateTime.UtcNow - started).TotalMilliseconds;
        }

        private static bool TryPostOnce(
            string endpoint,
            byte[] wavBytes,
            string fileName,
            out string text,
            out string error)
        {
            text = "";
            error = "";
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri))
            {
                error = "Endereço de transcrição inválido: " + endpoint;
                return false;
            }

            string boundary = "----tailmsg-" + Guid.NewGuid().ToString("N");
            byte[] body = BuildMultipartBody(boundary, wavBytes, fileName);

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = "POST";
                request.ContentType = "multipart/form-data; boundary=" + boundary;
                request.Accept = "application/json";
                request.ContentLength = body.Length;
                request.Timeout = TimeoutMilliseconds;
                request.ReadWriteTimeout = TimeoutMilliseconds;
                request.KeepAlive = false;
                request.Proxy = null;

                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string payload = reader.ReadToEnd();
                    string extracted;
                    if (!TranscriptionJson.TryExtractText(payload, out extracted))
                    {
                        error = "O serviço respondeu sem texto " +
                            "(o áudio pode não ter fala audível).";
                        return false;
                    }
                    text = extracted;
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = "Falha ao falar com " + endpoint + ": " + exception.Message;
                return false;
            }
        }

        private static byte[] BuildMultipartBody(
            string boundary,
            byte[] wavBytes,
            string fileName)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                string header =
                    "--" + boundary + "\r\n" +
                    "Content-Disposition: form-data; name=\"files\"; filename=\"" +
                    fileName + "\"\r\n" +
                    "Content-Type: audio/wav\r\n\r\n";
                byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(wavBytes, 0, wavBytes.Length);
                byte[] footerBytes = Encoding.UTF8.GetBytes(
                    "\r\n--" + boundary + "--\r\n");
                stream.Write(footerBytes, 0, footerBytes.Length);
                return stream.ToArray();
            }
        }
    }
}

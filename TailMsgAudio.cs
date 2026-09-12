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
            int width = target.ClientSize.Width - Math.Max(0, reserveRight);
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
        private bool layingOut;

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
            Padding = new Padding(1, 1, 0, 1);
            rowFont = new Font("Segoe UI", 9.5F);

            viewport = new Panel();
            viewport.Dock = DockStyle.Fill;
            viewport.BackColor = Color.White;
            Controls.Add(viewport);

            scrollBar = new VScrollBar();
            scrollBar.Dock = DockStyle.Right;
            scrollBar.Width = SystemInformation.VerticalScrollBarWidth;
            scrollBar.SmallChange = 24;
            scrollBar.Visible = false;
            scrollBar.Scroll += delegate { LayoutRows(); };
            Controls.Add(scrollBar);
        }

        // O layout pode rodar antes do viewport existir (o próprio construtor
        // adiciona controles), então a largura/altura precisam de guarda.
        private int ContentWidth
        {
            get
            {
                int width = viewport == null ? ClientSize.Width : viewport.ClientSize.Width;
                return Math.Max(120, width - 18);
            }
        }

        private int ViewportHeight
        {
            get { return viewport == null ? ClientSize.Height : viewport.ClientSize.Height; }
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

        public InboxImageRow AppendImage(string prefix, byte[] imageBytes, string filePath)
        {
            InboxImageRow row = new InboxImageRow(prefix, imageBytes, filePath, PlayerSize, rowFont);
            AddRow(row);
            return row;
        }

        public InboxAudioRow AppendAudio(
            string prefix,
            AudioPayload audio,
            string operationId)
        {
            InboxAudioRow row = new InboxAudioRow(prefix, audio, operationId, PlayerSize, rowFont);
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

        // Menu de contexto das linhas: deletar, deletar para todos e (nos
        // áudios) a transcrição entre aspas e em itálico.
        public void AttachMenu(
            Control row,
            long seq,
            string operationId,
            bool sent,
            string transcription)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = rowFont;

            if (!String.IsNullOrEmpty(transcription))
            {
                ToolStripMenuItem quote = new ToolStripMenuItem("\"" + transcription + "\"");
                quote.Font = new Font(rowFont, FontStyle.Italic);
                quote.ForeColor = Color.FromArgb(75, 85, 99);
                quote.Enabled = false;
                menu.Items.Add(quote);
                menu.Items.Add(new ToolStripSeparator());
            }

            InboxRowBase rowBase = row as InboxRowBase;
            string address = rowBase == null ? "" : rowBase.Address;

            ToolStripMenuItem delete = new ToolStripMenuItem("Deletar");
            delete.Click += delegate { RaiseDelete(seq, operationId, address, false); };
            menu.Items.Add(delete);

            if (sent && !String.IsNullOrEmpty(operationId))
            {
                ToolStripMenuItem everywhere = new ToolStripMenuItem("Deletar para todos");
                everywhere.Click += delegate { RaiseDelete(seq, operationId, address, true); };
                menu.Items.Add(everywhere);
            }

            row.ContextMenuStrip = menu;
            foreach (Control child in row.Controls)
            {
                child.ContextMenuStrip = menu;
            }
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
            viewport.Controls.Remove(row);
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
            if (viewport == null) return new Control[0];
            Control[] children = new Control[viewport.Controls.Count];
            viewport.Controls.CopyTo(children, 0);
            return children;
        }

        public void Clear()
        {
            if (viewport == null) return;
            if (activeRow != null)
            {
                activeRow.StopPlayback();
                activeRow = null;
            }
            foreach (Control control in Snapshot())
            {
                InboxAudioRow audioRow = control as InboxAudioRow;
                if (audioRow != null) audioRow.StopPlayback();
                viewport.Controls.Remove(control);
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
            if (viewport == null) return;
            viewport.SuspendLayout();
            row.Width = ContentWidth;
            viewport.Controls.Add(row);
            viewport.ResumeLayout();
            LayoutRows();
            ScrollToBottom();
        }

        // Mantém a última mensagem visível, como em um aplicativo de conversa.
        private void ScrollToBottom()
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
            LayoutRows();
        }

        // Posiciona as linhas e ajusta a barra em passadas curtas: a largura
        // útil depende da barra, e a altura do conteúdo depende da largura.
        private void LayoutRows()
        {
            if (layingOut || viewport == null || scrollBar == null) return;
            layingOut = true;
            try
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    bool visibleBefore = scrollBar.Visible;
                    int width = ContentWidth;
                    int start = -scrollBar.Value;
                    int top = start;
                    foreach (Control control in Snapshot())
                    {
                        control.Width = width;
                        // O texto enviado precisa se realinhar à direita sempre
                        // que a largura muda (inclusive ao abrir a barra).
                        InboxTextRow textRow = control as InboxTextRow;
                        if (textRow != null) textRow.LayoutRow();
                        control.Location = new Point(0, top);
                        top += control.Height + 2;
                    }

                    int contentHeight = top - start;
                    int viewportHeight = ViewportHeight;
                    int overflow = Math.Max(0, contentHeight - viewportHeight);
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
            }
            finally
            {
                layingOut = false;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int reserved = scrollBar != null && scrollBar.Visible ? scrollBar.Width : 0;
            BoxBorder.Draw(e.Graphics, this, reserved);
        }
    }

    // Base das linhas que o menu de contexto pode apagar.
    internal abstract class InboxRowBase : Panel
    {
        public long Seq { get; set; }
        public string OperationId { get; set; }
        public string Address { get; set; }
        public bool Sent { get; set; }
    }

    // Linha de mensagem de texto: recebida à esquerda, enviada à direita.
    internal sealed class InboxTextRow : InboxRowBase
    {
        private readonly Label label;

        public InboxTextRow(string who, string text, string time, bool sent, Font font)
        {
            BackColor = Color.White;
            label = new Label();
            label.Font = font;
            label.AutoSize = true;
            label.ForeColor = sent ? InboxPanel.SentColor : Color.FromArgb(31, 41, 55);
            label.Text = who + ": " + text + " [" + time + "]";
            Controls.Add(label);
            Resize += delegate { LayoutRow(); };
            LayoutRow();
        }

        public string Text
        {
            get { return label.Text; }
        }

        private bool layingOutRow;

        public void LayoutRow()
        {
            if (layingOutRow) return;
            layingOutRow = true;
            try
            {
            int available = Parent == null ? Width : Parent.ClientSize.Width;
            available = Math.Max(160, available - 8);
            label.MaximumSize = new Size(available, 0);
            int left = Sent ? Math.Max(0, available - label.Width) : 0;
            label.Location = new Point(left, 0);
            Width = available;
            Height = Math.Max(18, label.Height + 2);
            }
            finally
            {
                layingOutRow = false;
            }
        }
    }

    // Uma linha de imagem do histórico: prefixo e botão que abre a imagem no
    // aplicativo padrão do Windows.
    internal sealed class InboxImageRow : InboxRowBase
    {
        private static Image imageGlyph;
        private readonly byte[] imageBytes;
        private readonly string filePath;
        private readonly Label prefixLabel;
        private readonly IconButton openButton;

        public InboxImageRow(string prefix, byte[] imageBytes, string filePath, int iconSize, Font font)
        {
            this.imageBytes = imageBytes;
            this.filePath = filePath;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.White;

            prefixLabel = new Label();
            prefixLabel.AutoSize = true;
            prefixLabel.Font = font;
            prefixLabel.ForeColor = Color.FromArgb(31, 41, 55);
            prefixLabel.Text = prefix;
            prefixLabel.Margin = new Padding(0, 4, 4, 0);
            Controls.Add(prefixLabel);

            openButton = new IconButton();
            openButton.Glyph = IconGlyph.None;
            openButton.SourceImage = LoadImageGlyph();
            openButton.CircleColor = Color.White;
            openButton.CircleOutline = Color.FromArgb(196, 202, 210);
            openButton.Size = new Size(iconSize, iconSize);
            openButton.Location = new Point(prefixLabel.Right + 4, 0);
            openButton.Enabled = HasImage();
            openButton.AccessibleName = "Abrir imagem";
            openButton.Click += delegate { OpenImage(); };
            Controls.Add(openButton);

            Height = Math.Max(iconSize + 2, prefixLabel.Height + 4);
            Resize += delegate { LayoutRow(); };
            LayoutRow();
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

        private bool layingOutRow;

        private void LayoutRow()
        {
            if (layingOutRow) return;
            layingOutRow = true;
            try
            {
                int available = Parent == null ? Width : Parent.ClientSize.Width;
                available = Math.Max(160, available - 30);
                prefixLabel.MaximumSize = new Size(Math.Max(80, available - openButton.Width - 12), 0);
                openButton.Location = new Point(prefixLabel.Right + 4, 0);
                Height = Math.Max(openButton.Height + 2, prefixLabel.Height + 4);
            }
            finally
            {
                layingOutRow = false;
            }
        }

        // Grava os bytes em um arquivo temporário e entrega ao sistema, que
        // abre com o aplicativo padrão do usuário (mesmo caminho do Explorer).
        private bool HasImage()
        {
            return (imageBytes != null && imageBytes.Length > 0) ||
                (!String.IsNullOrEmpty(filePath) && File.Exists(filePath));
        }

        private void OpenImage()
        {
            // Imagem do histórico persistente: o arquivo já está em disco.
            if (!String.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
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
                string fileName = "imagem-" + ShortHash(imageBytes) + ".png";
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
        private readonly Label prefixLabel;
        private readonly Label transcriptionLabel;
        private readonly WavePlayer player = new WavePlayer();
        private readonly System.Windows.Forms.Timer ticker;

        public event EventHandler PlayRequested;

        // Identificador da linha no histórico persistente (0 quando não gravada).
        public long HistorySeq { get; set; }

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
            string prefix,
            AudioPayload payload,
            string operationId,
            int playerSize,
            Font font)
        {
            audio = payload;
            OperationId = operationId;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.White;

            string loadError = "";
            bool loaded = payload != null && payload.WavBytes != null &&
                player.Load(payload.WavBytes, out loadError);

            prefixLabel = new Label();
            prefixLabel.AutoSize = true;
            prefixLabel.Font = font;
            prefixLabel.ForeColor = Color.FromArgb(31, 41, 55);
            prefixLabel.Text = prefix;
            prefixLabel.Margin = new Padding(0, 4, 4, 0);
            Controls.Add(prefixLabel);

            playButton = new IconButton();
            playButton.CircleColor = Color.FromArgb(242, 207, 55);
            playButton.CircleOutline = Color.FromArgb(196, 157, 0);
            playButton.Glyph = IconGlyph.Play;
            playButton.Size = new Size(playerSize, playerSize);
            playButton.Location = new Point(prefixLabel.Right + 2, 0);
            playButton.Enabled = loaded;
            playButton.AccessibleName = "Reproduzir áudio";
            playButton.Click += delegate { Toggle(); };
            Controls.Add(playButton);

            transcriptionLabel = new Label();
            transcriptionLabel.AutoSize = true;
            transcriptionLabel.Font = font;
            transcriptionLabel.ForeColor = Color.FromArgb(75, 85, 99);
            transcriptionLabel.Text = "";
            Controls.Add(transcriptionLabel);

            Height = Math.Max(playerSize + 2, prefixLabel.Height + 4);
            Resize += delegate { LayoutRow(); };
            LayoutRow();

            ticker = new System.Windows.Forms.Timer();
            ticker.Interval = 150;
            ticker.Tick += delegate { Tick(); };
            ticker.Start();
        }

        // Texto da transcrição, exibido depois do botão de play.
        // A transcrição não aparece no histórico: fica guardada para o menu de
        // contexto (item entre aspas, em itálico).
        public string Transcription { get; private set; }

        public void SetTranscription(string text)
        {
            Transcription = text ?? "";
        }

        public void StopPlayback()
        {
            player.Stop();
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
            }
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

        private bool layingOutRow;

        private void LayoutRow()
        {
            if (layingOutRow) return;
            layingOutRow = true;
            SuspendLayout();
            try
            {
            // Espaço disponível na linha do histórico: o que sobra depois do
            // prefixo e do botão. A transcrição quebra dentro dele, para a
            // caixa nunca precisar de barra de rolagem horizontal.
            int available = Parent == null ? Width : Parent.ClientSize.Width;
            available = Math.Max(160, available - 30);
            int reserved = prefixLabel.Width + playButton.Width + 12;
            transcriptionLabel.MaximumSize = new Size(Math.Max(60, available - reserved), 0);

            Control[] children = new Control[Controls.Count];
            Controls.CopyTo(children, 0);
            int left = 0;
            int height = 20;
            foreach (Control child in children)
            {
                child.Location = new Point(left, child == playButton ? 0 : 3);
                left += child.Width + 4;
                if (child.Height > height) height = child.Height;
            }
            Width = Math.Min(Math.Max(140, left), available);
            Height = Math.Max(22, height + 2);
            }
            finally
            {
                ResumeLayout();
                layingOutRow = false;
            }
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
        private const int TimeoutMilliseconds = 120000;
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

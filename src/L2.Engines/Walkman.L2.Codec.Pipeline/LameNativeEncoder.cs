using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Walkman.L2.Codec.Pipeline;

/// <summary>
/// Collectible-ALC-safe dynamic loader for external unmodified LGPL-2.0 libmp3lame.dll in /codecs/.
/// Avoids AppDomain.CurrentDomain.AssemblyResolve hooks so PluginLoadContext unloads with zero leaks.
/// </summary>
internal static unsafe class LameNativeEncoder
{
    private static readonly ReaderWriterLockSlim RwLock = new(LockRecursionPolicy.NoRecursion);
    private static nint _libHandle;

    private static delegate* unmanaged[Cdecl]<nint> _lameInit;
    private static delegate* unmanaged[Cdecl]<nint, int, int> _setInSampleRate;
    private static delegate* unmanaged[Cdecl]<nint, int, int> _setNumChannels;
    private static delegate* unmanaged[Cdecl]<nint, int, int> _setBrate;
    private static delegate* unmanaged[Cdecl]<nint, int, int> _setQuality;
    private static delegate* unmanaged[Cdecl]<nint, int> _initParams;
    private static delegate* unmanaged[Cdecl]<nint, short*, int, byte*, int, int> _encodeInterleaved;
    private static delegate* unmanaged[Cdecl]<nint, byte*, int, int> _encodeFlush;
    private static delegate* unmanaged[Cdecl]<nint, byte*, nuint, nuint> _getLameTagFrame;
    private static delegate* unmanaged[Cdecl]<nint, int> _lameClose;

    public static void EncodeWaveProviderToMp3(
        IWaveProvider pcm16Stereo44k,
        string destMp3Path,
        int bitrateKbps,
        CancellationToken ct)
    {
        EnsureLoaded();

        RwLock.EnterReadLock();
        try
        {
            if (_libHandle == 0 || _lameInit == null)
            {
                throw new InvalidOperationException("LAME native library is not loaded.");
            }

            nint gfp = _lameInit();
            if (gfp == 0)
            {
                throw new InvalidOperationException("lame_init() returned null handle.");
            }

            try
        {
            if (_setInSampleRate(gfp, pcm16Stereo44k.WaveFormat.SampleRate) < 0 ||
                _setNumChannels(gfp, pcm16Stereo44k.WaveFormat.Channels) < 0 ||
                _setBrate(gfp, bitrateKbps) < 0 ||
                _setQuality(gfp, 2) < 0 ||
                _initParams(gfp) < 0)
            {
                throw new InvalidOperationException("Failed to initialize LAME MP3 encoder parameters.");
            }

            const int pcmChunkBytes = 16384 * 4; // 16384 stereo 16-bit frames
            byte[] pcmBuffer = new byte[pcmChunkBytes];
            int maxOutBytes = (int)(1.25 * 16384) + 7200;
            byte[] mp3Buffer = new byte[maxOutBytes];

            using var outFs = new FileStream(destMp3Path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536);

            int bytesRead;
            while ((bytesRead = pcm16Stereo44k.Read(pcmBuffer, 0, pcmBuffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int samplesPerChannel = bytesRead / 4;
                if (samplesPerChannel == 0)
                {
                    continue;
                }

                int encodedBytes;
                fixed (byte* pPcm = pcmBuffer)
                fixed (byte* pMp3 = mp3Buffer)
                {
                    encodedBytes = _encodeInterleaved(gfp, (short*)pPcm, samplesPerChannel, pMp3, mp3Buffer.Length);
                }

                if (encodedBytes < 0)
                {
                    throw new InvalidOperationException($"lame_encode_buffer_interleaved failed with code {encodedBytes}.");
                }

                if (encodedBytes > 0)
                {
                    outFs.Write(mp3Buffer, 0, encodedBytes);
                }
            }

            int flushedBytes;
            fixed (byte* pMp3 = mp3Buffer)
            {
                flushedBytes = _encodeFlush(gfp, pMp3, mp3Buffer.Length);
            }

            if (flushedBytes > 0)
            {
                outFs.Write(mp3Buffer, 0, flushedBytes);
            }

            // Update Xing/Info LAME header frame at offset 0 if present.
            byte[] tagFrame = new byte[2048];
            nuint tagSize;
            fixed (byte* pTag = tagFrame)
            {
                tagSize = _getLameTagFrame(gfp, pTag, (nuint)tagFrame.Length);
            }

            if (tagSize > 0 && tagSize <= (nuint)tagFrame.Length && outFs.Length >= (long)tagSize)
            {
                outFs.Position = 0;
                outFs.Write(tagFrame, 0, (int)tagSize);
            }

            outFs.Flush();
        }
            finally
            {
                _lameClose(gfp);
            }
        }
        finally
        {
            RwLock.ExitReadLock();
        }
    }

    public static void UnloadLibrary()
    {
        RwLock.EnterWriteLock();
        try
        {
            if (_libHandle != 0)
            {
                _lameInit = null;
                _setInSampleRate = null;
                _setNumChannels = null;
                _setBrate = null;
                _setQuality = null;
                _initParams = null;
                _encodeInterleaved = null;
                _encodeFlush = null;
                _getLameTagFrame = null;
                _lameClose = null;
                NativeLibrary.Free(_libHandle);
                _libHandle = 0;
            }
        }
        finally
        {
            RwLock.ExitWriteLock();
        }
    }

    private static void EnsureLoaded()
    {
        RwLock.EnterUpgradeableReadLock();
        try
        {
            if (_libHandle != 0)
            {
                return;
            }

            RwLock.EnterWriteLock();
            try
            {
                if (_libHandle != 0)
                {
                    return;
                }

                string dllPath = ResolveLameDllPath();
                nint handle = NativeLibrary.Load(dllPath);
                try
                {
                    _lameInit = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(handle, "lame_init");
                    _setInSampleRate = (delegate* unmanaged[Cdecl]<nint, int, int>)NativeLibrary.GetExport(handle, "lame_set_in_samplerate");
                    _setNumChannels = (delegate* unmanaged[Cdecl]<nint, int, int>)NativeLibrary.GetExport(handle, "lame_set_num_channels");
                    _setBrate = (delegate* unmanaged[Cdecl]<nint, int, int>)NativeLibrary.GetExport(handle, "lame_set_brate");
                    _setQuality = (delegate* unmanaged[Cdecl]<nint, int, int>)NativeLibrary.GetExport(handle, "lame_set_quality");
                    _initParams = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(handle, "lame_init_params");
                    _encodeInterleaved = (delegate* unmanaged[Cdecl]<nint, short*, int, byte*, int, int>)NativeLibrary.GetExport(handle, "lame_encode_buffer_interleaved");
                    _encodeFlush = (delegate* unmanaged[Cdecl]<nint, byte*, int, int>)NativeLibrary.GetExport(handle, "lame_encode_flush");
                    _getLameTagFrame = (delegate* unmanaged[Cdecl]<nint, byte*, nuint, nuint>)NativeLibrary.GetExport(handle, "lame_get_lametag_frame");
                    _lameClose = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(handle, "lame_close");
                    _libHandle = handle;
                }
                catch
                {
                    _lameInit = null;
                    _setInSampleRate = null;
                    _setNumChannels = null;
                    _setBrate = null;
                    _setQuality = null;
                    _initParams = null;
                    _encodeInterleaved = null;
                    _encodeFlush = null;
                    _getLameTagFrame = null;
                    _lameClose = null;
                    NativeLibrary.Free(handle);
                    throw;
                }
            }
            finally
            {
                RwLock.ExitWriteLock();
            }
        }
        finally
        {
            RwLock.ExitUpgradeableReadLock();
        }
    }

    private static string ResolveLameDllPath()
    {
        string bitnessName = IntPtr.Size == 8 ? "libmp3lame.dll" : "libmp3lame.32.dll";
        string[] candidateNames = IntPtr.Size == 8
            ? [bitnessName, "libmp3lame.64.dll"]
            : [bitnessName, "libmp3lame.dll"];

        string[] roots =
        [
            Path.GetDirectoryName(typeof(LameNativeEncoder).Assembly.Location) ?? AppContext.BaseDirectory,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        ];

        foreach (string start in roots)
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                foreach (string name in candidateNames)
                {
                    string inCodecs = Path.Combine(current.FullName, "codecs", name);
                    if (File.Exists(inCodecs))
                    {
                        return inCodecs;
                    }

                    string direct = Path.Combine(current.FullName, name);
                    if (File.Exists(direct))
                    {
                        return direct;
                    }
                }

                current = current.Parent;
            }
        }

        throw new DllNotFoundException($"Could not locate external LGPL '{bitnessName}' in /codecs/ or plugin directory.");
    }
}

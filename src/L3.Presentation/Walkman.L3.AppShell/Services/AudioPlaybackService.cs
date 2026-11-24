using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Walkman.L3.AppShell.Core;

namespace Walkman.L3.AppShell.Services;

public sealed class AudioPlaybackService : IDisposable
{
    private readonly DispatcherTimer _tickTimer;
    private PlaybackSession? _session;
    private CancellationTokenSource? _omaDecodeCts;
    private WindowsSmtcBridge? _smtcBridge;
    private WindowsGlobalMediaKeyHook? _globalKeyHook;
    private AudioTrackItem? _currentTrack;
    private bool _isPlaying;
    private bool _isDecodingOma;
    private double _elapsedSeconds;
    private double _volume = 0.85;
    private bool _isMuted;
    private DateTime _lastMediaKeyUtc = DateTime.MinValue;

    public AudioPlaybackService()
    {
        _tickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _tickTimer.Tick += OnTick;
    }

    public AudioTrackItem? CurrentTrack => _currentTrack;
    public bool IsPlaying => _isPlaying;
    public int ElapsedSeconds => (int)Math.Floor(_elapsedSeconds);
    public bool IsSmtcActive => _smtcBridge is not null;
    internal bool IsSessionSeeking => _session?.IsSeeking ?? false;
    internal PlaybackState? SessionPlaybackState => _session?.WaveOut.PlaybackState;
    public double Volume
    {
        get => _volume;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_volume - clamped) > 0.001)
            {
                _volume = clamped;
                ApplyVolume();
                StateChanged?.Invoke();
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted != value)
            {
                _isMuted = value;
                ApplyVolume();
                StateChanged?.Invoke();
            }
        }
    }

    private float EffectiveVolume => _isMuted ? 0f : (float)Math.Clamp(_volume, 0.0, 1.0);

    public event Action? StateChanged;
    public event Action? TrackEnded;
    public event Action? NextTrackRequested;
    public event Action? PreviousTrackRequested;

    public Func<AudioTrackItem, CancellationToken, Task<Stream?>>? OmaStreamResolver { get; set; }

    public void AttachWindowHandleForSmtc(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        if (_smtcBridge is null)
        {
            try
            {
                _smtcBridge = WindowsSmtcBridge.TryCreate(hwnd, OnSmtcButtonPressed);
                UpdateSmtcState();
            }
            catch
            {
                _smtcBridge = null;
            }
        }

        if (_globalKeyHook is null)
        {
            _globalKeyHook = WindowsGlobalMediaKeyHook.TryInstall(button =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_currentTrack is null || !TryConsumeExternalMediaKeyGuard())
                    {
                        return;
                    }

                    if (button == -1)
                    {
                        TogglePlayPause();
                    }
                    else
                    {
                        DispatchMediaButton(button);
                    }
                });
            });
        }
    }

    public bool TryConsumeExternalMediaKeyGuard()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastMediaKeyUtc).TotalMilliseconds < 180)
        {
            return false;
        }

        _lastMediaKeyUtc = now;
        return true;
    }

    private void OnSmtcButtonPressed(int button)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!TryConsumeExternalMediaKeyGuard())
            {
                return;
            }

            DispatchMediaButton(button);
        });
    }

    private void DispatchMediaButton(int button)
    {
        switch (button)
        {
            case 0: // Play
                if (!_isPlaying) TogglePlayPause();
                break;
            case 1: // Pause
                if (_isPlaying) TogglePlayPause();
                break;
            case 2: // Stop
                Stop();
                break;
            case 6: // Next
                NextTrackRequested?.Invoke();
                break;
            case 7: // Previous
                PreviousTrackRequested?.Invoke();
                break;
        }
    }

    public void PlayOrToggle(AudioTrackItem track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (_currentTrack?.Id == track.Id)
        {
            TogglePlayPause();
            return;
        }

        StopAudioEngine();
        _currentTrack = track;
        _elapsedSeconds = 0;

        if (!string.IsNullOrWhiteSpace(track.SourceFilePath) &&
            track.SourceFilePath.EndsWith(".oma", StringComparison.OrdinalIgnoreCase) &&
            OmaStreamResolver is not null)
        {
            _isDecodingOma = true;
            _isPlaying = true;
            _tickTimer.Start();
            StateChanged?.Invoke();
            UpdateSmtcState();
            _ = StartOmaStreamPlaybackAsync(track);
            return;
        }

        bool isRealFile = !track.IsDemoItem && !string.IsNullOrWhiteSpace(track.SourceFilePath);
        bool startedRealAudio = TryStartRealPlayback(track.SourceFilePath);
        if (isRealFile && !startedRealAudio)
        {
            _isPlaying = false;
            _tickTimer.Stop();
            StateChanged?.Invoke();
            UpdateSmtcState();
            return;
        }

        _isPlaying = true;
        _tickTimer.Start();
        StateChanged?.Invoke();
        UpdateSmtcState();
    }

    private async Task StartOmaStreamPlaybackAsync(AudioTrackItem track)
    {
        _omaDecodeCts?.Cancel();
        _omaDecodeCts?.Dispose();
        _omaDecodeCts = new CancellationTokenSource();
        var ct = _omaDecodeCts.Token;

        try
        {
            var resolver = OmaStreamResolver;
            if (resolver is null)
            {
                return;
            }

            Stream? wavStream = await resolver(track, ct).ConfigureAwait(false);
            if (wavStream is null || ct.IsCancellationRequested)
            {
                wavStream?.Dispose();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_currentTrack?.Id == track.Id && !ct.IsCancellationRequested)
                    {
                        _isDecodingOma = false;
                        _isPlaying = false;
                        _tickTimer.Stop();
                        StateChanged?.Invoke();
                        UpdateSmtcState();
                    }
                });
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || _currentTrack?.Id != track.Id)
                {
                    wavStream.Dispose();
                    return;
                }

                _isDecodingOma = false;
                try
                {
                    var wavReader = new WaveFileReader(wavStream);
                    var channel = new SampleChannel(wavReader, true)
                    {
                        Volume = EffectiveVolume
                    };
                    var session = new PlaybackSession(wavReader, channel, wavStream, _isPlaying);
                    _session = session;
                    if (_isPlaying)
                    {
                        session.Play();
                    }
                }
                catch
                {
                    StopAudioEngine();
                    _isPlaying = false;
                    _tickTimer.Stop();
                    StateChanged?.Invoke();
                    UpdateSmtcState();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    public void TogglePlayPause()
    {
        if (_currentTrack is null)
        {
            return;
        }

        if (_isPlaying)
        {
            if (_session is not null)
            {
                _session.DesiredPlaying = false;
                _session.Pause();
            }
            _isPlaying = false;
            _tickTimer.Stop();
        }
        else
        {
            if (_session is not null)
            {
                _session.DesiredPlaying = true;
                _session.Play();
            }
            _isPlaying = true;
            _tickTimer.Start();
        }

        StateChanged?.Invoke();
        UpdateSmtcState();
    }

    public void Seek(double seconds)
    {
        if (_currentTrack is null)
        {
            return;
        }

        double clamped = Math.Clamp(seconds, 0, Math.Max(1, _currentTrack.DurationSec));
        _elapsedSeconds = clamped;

        _session?.EnqueueSeek(clamped);

        StateChanged?.Invoke();
        UpdateSmtcState();
    }

    public void Stop()
    {
        StopAudioEngine();
        _tickTimer.Stop();
        _isPlaying = false;
        _elapsedSeconds = 0;
        _currentTrack = null;
        StateChanged?.Invoke();
        UpdateSmtcState();
    }

    private bool TryStartRealPlayback(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath) ||
            sourcePath.EndsWith(".oma", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var reader = new AudioFileReader(sourcePath)
            {
                Volume = EffectiveVolume
            };
            var session = new PlaybackSession(reader, sampleChannel: null, ownedStream: null, desiredPlaying: true);
            _session = session;
            session.Play();
            return true;
        }
        catch
        {
            StopAudioEngine();
        }

        if (FlacWaveStream.LooksLikeFlacFile(sourcePath))
        {
            try
            {
                var flacReader = new FlacWaveStream(sourcePath);
                var channel = new SampleChannel(flacReader, true)
                {
                    Volume = EffectiveVolume
                };
                var session = new PlaybackSession(flacReader, channel, ownedStream: null, desiredPlaying: true);
                _session = session;
                session.Play();
                return true;
            }
            catch
            {
                StopAudioEngine();
            }
        }

        return false;
    }

    private void ApplyVolume()
    {
        var session = _session;
        if (session?.Reader is AudioFileReader reader)
        {
            reader.Volume = EffectiveVolume;
        }
        else if (session?.SampleChannel is not null)
        {
            session.SampleChannel.Volume = EffectiveVolume;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_isPlaying || _currentTrack is null)
        {
            return;
        }

        var session = _session;
        if (session is not null)
        {
            if (!session.IsSeeking && Monitor.TryEnter(session.SyncLock))
            {
                try
                {
                    if (!session.IsDisposed)
                    {
                        _elapsedSeconds = session.Reader.CurrentTime.TotalSeconds;
                    }
                }
                finally
                {
                    Monitor.Exit(session.SyncLock);
                }
            }

            if (!session.IsSeeking && session.WaveOut.PlaybackState == PlaybackState.Stopped)
            {
                if (session.ReachedEndOfStream || _elapsedSeconds >= Math.Max(1, _currentTrack.DurationSec - 1))
                {
                    TrackEnded?.Invoke();
                    return;
                }

                if (session.DesiredPlaying)
                {
                    session.EnsurePlaying();
                }
            }
        }
        else if (_isDecodingOma)
        {
            return;
        }
        else
        {
            _elapsedSeconds += 0.25;
            if (_elapsedSeconds >= Math.Max(1, _currentTrack.DurationSec))
            {
                _elapsedSeconds = 0;
                TrackEnded?.Invoke();
                return;
            }
        }

        StateChanged?.Invoke();
        UpdateSmtcState();
    }

    private void StopAudioEngine(bool synchronousDispose = false)
    {
        _omaDecodeCts?.Cancel();
        _omaDecodeCts?.Dispose();
        _omaDecodeCts = null;
        _isDecodingOma = false;

        var oldSession = _session;
        _session = null;
        if (oldSession is not null)
        {
            oldSession.MarkDisposed();
            if (synchronousDispose)
            {
                oldSession.Dispose();
            }
            else
            {
                _ = Task.Run(oldSession.Dispose);
            }
        }
    }

    private void UpdateSmtcState()
    {
        _smtcBridge?.Update(_currentTrack, _isPlaying);
    }

    public void Dispose()
    {
        _tickTimer.Stop();
        StopAudioEngine(synchronousDispose: true);
        _globalKeyHook?.Dispose();
        _globalKeyHook = null;
        _smtcBridge?.Dispose();
        _smtcBridge = null;
    }

    private sealed class PlaybackSession : IDisposable
    {
        public readonly object SyncLock = new();
        private readonly object _seekQueueLock = new();
        private readonly object _waveOutLock = new();
        public readonly WaveStream Reader;
        public readonly SampleChannel? SampleChannel;
        public readonly WaveOutEvent WaveOut;
        private readonly Stream? _ownedStream;
        private readonly ThreadSafeSampleProvider _provider;
        private int _seekGeneration;
        private double _pendingSeekSeconds = double.NaN;
        private bool _seekWorkerRunning;
        public volatile bool IsDisposed;
        public volatile bool IsSeeking;
        public volatile bool DesiredPlaying;
        public volatile bool ReachedEndOfStream;

        public PlaybackSession(WaveStream reader, SampleChannel? sampleChannel, Stream? ownedStream, bool desiredPlaying)
        {
            Reader = reader;
            SampleChannel = sampleChannel;
            _ownedStream = ownedStream;
            DesiredPlaying = desiredPlaying;
            ISampleProvider source = sampleChannel as ISampleProvider ?? (ISampleProvider)reader;
            _provider = new ThreadSafeSampleProvider(source, SyncLock, () => IsSeeking, () => ReachedEndOfStream = true);
            WaveOut = new WaveOutEvent { DesiredLatency = 150 };
            try
            {
                WaveOut.Init(_provider);
            }
            catch
            {
                try { WaveOut.Dispose(); } catch { }
                try { Reader.Dispose(); } catch { }
                try { _ownedStream?.Dispose(); } catch { }
                throw;
            }
        }

        public void Play()
        {
            lock (_waveOutLock)
            {
                if (!IsDisposed)
                {
                    WaveOut.Play();
                }
            }
        }

        public void Pause()
        {
            lock (_waveOutLock)
            {
                if (!IsDisposed)
                {
                    WaveOut.Pause();
                }
            }
        }

        public void EnsurePlaying()
        {
            lock (_waveOutLock)
            {
                if (!IsDisposed && DesiredPlaying && WaveOut.PlaybackState != PlaybackState.Playing)
                {
                    WaveOut.Play();
                }
            }
        }

        public void MarkDisposed()
        {
            IsDisposed = true;
            DesiredPlaying = false;
            IsSeeking = false;
            Interlocked.Increment(ref _seekGeneration);
            lock (_seekQueueLock)
            {
                _pendingSeekSeconds = double.NaN;
            }

            _provider.MarkDisposed();
        }

        public void EnqueueSeek(double seconds)
        {
            if (IsDisposed)
            {
                return;
            }

            Interlocked.Increment(ref _seekGeneration);
            lock (_seekQueueLock)
            {
                _pendingSeekSeconds = seconds;
                IsSeeking = true;
                if (!_seekWorkerRunning)
                {
                    _seekWorkerRunning = true;
                    _ = Task.Run(DrainSeekQueue);
                }
            }
        }

        private void DrainSeekQueue()
        {
            try
            {
                while (true)
                {
                    double target;
                    lock (_seekQueueLock)
                    {
                        if (IsDisposed || double.IsNaN(_pendingSeekSeconds))
                        {
                            IsSeeking = false;
                            _seekWorkerRunning = false;
                            break;
                        }

                        target = _pendingSeekSeconds;
                        _pendingSeekSeconds = double.NaN;
                    }

                    int targetGen = Volatile.Read(ref _seekGeneration);

                    lock (SyncLock)
                    {
                        if (IsDisposed || targetGen != Volatile.Read(ref _seekGeneration))
                        {
                            continue;
                        }

                        try
                        {
                            ReachedEndOfStream = false;
                            double totalSec = Reader.TotalTime.TotalSeconds;
                            double safeSec = totalSec > 0.30
                                ? Math.Clamp(target, 0, Math.Max(0, totalSec - 0.20))
                                : Math.Clamp(target, 0, Math.Max(0, totalSec));

                            if (Reader is WaveFileReader or FlacWaveStream)
                            {
                                long pos = (long)(safeSec * Reader.WaveFormat.AverageBytesPerSecond);
                                int align = Math.Max(1, Reader.WaveFormat.BlockAlign);
                                pos -= pos % align;
                                long maxPos = Math.Max(0, Reader.Length - align);
                                Reader.Position = Math.Clamp(pos, 0, maxPos);
                            }
                            else
                            {
                                Reader.CurrentTime = TimeSpan.FromSeconds(safeSec);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            finally
            {
                lock (_seekQueueLock)
                {
                    IsSeeking = false;
                    _seekWorkerRunning = false;
                }
            }

            if (!IsDisposed && DesiredPlaying)
            {
                try
                {
                    EnsurePlaying();
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            MarkDisposed();
            lock (_waveOutLock)
            {
                try { WaveOut.Stop(); } catch { }
                try { WaveOut.Dispose(); } catch { }
            }

            lock (SyncLock)
            {
                try { Reader.Dispose(); } catch { }
                try { _ownedStream?.Dispose(); } catch { }
            }
        }
    }

    private sealed class ThreadSafeSampleProvider(
        ISampleProvider inner,
        object syncLock,
        Func<bool> isSeekingGetter,
        Action onEndOfStream) : ISampleProvider
    {
        private volatile bool _disposed;

        public void MarkDisposed() => _disposed = true;

        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_disposed)
            {
                return 0;
            }

            if (isSeekingGetter())
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            if (!Monitor.TryEnter(syncLock, 15))
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            try
            {
                if (_disposed)
                {
                    return 0;
                }

                if (isSeekingGetter())
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                try
                {
                    int read = inner.Read(buffer, offset, count);
                    if (read == 0)
                    {
                        if (isSeekingGetter())
                        {
                            Array.Clear(buffer, offset, count);
                            return count;
                        }

                        onEndOfStream();
                    }

                    return read;
                }
                catch
                {
                    if (isSeekingGetter())
                    {
                        Array.Clear(buffer, offset, count);
                        return count;
                    }

                    return 0;
                }
            }
            finally
            {
                Monitor.Exit(syncLock);
            }
        }
    }

    private sealed unsafe class WindowsSmtcBridge : IDisposable
    {
        private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IidAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
        private static readonly Guid IidButtonPressedHandler = new("0557E996-7B23-5BAE-AA81-EA0D671143A4");

        private static readonly IntPtr* HandlerVtable = AllocateHandlerVtable();

        private readonly IntPtr _mediaPlayerPtr;
        private readonly IntPtr _smtcPtr;
        private readonly IntPtr _handlerPtr;
        private readonly long _buttonToken;
        private bool _disposed;
        private string? _lastTrackId;
        private string? _lastTitle;
        private string? _lastArtist;
        private string? _lastAlbum;
        private int _lastStatus = -1;

        [StructLayout(LayoutKind.Sequential)]
        private struct ButtonHandlerObject
        {
            public IntPtr* Vtable;
            public int RefCount;
            public IntPtr CallbackHandle;
        }

        private WindowsSmtcBridge(IntPtr mediaPlayerPtr, IntPtr smtcPtr, IntPtr handlerPtr, long buttonToken)
        {
            _mediaPlayerPtr = mediaPlayerPtr;
            _smtcPtr = smtcPtr;
            _handlerPtr = handlerPtr;
            _buttonToken = buttonToken;
        }

        private static IntPtr* AllocateHandlerVtable()
        {
            var vtbl = (IntPtr*)NativeMemory.Alloc((nuint)(4 * IntPtr.Size));
            vtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&HandlerQueryInterface;
            vtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&HandlerAddRef;
            vtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&HandlerRelease;
            vtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&HandlerInvoke;
            return vtbl;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int HandlerQueryInterface(IntPtr thisPtr, Guid* riid, IntPtr* ppv)
        {
            if (ppv == null || riid == null)
            {
                return unchecked((int)0x80004003); // E_POINTER
            }

            Guid iid = *riid;
            if (iid == IidUnknown || iid == IidAgileObject || iid == IidButtonPressedHandler)
            {
                *ppv = thisPtr;
                AddRefCore(thisPtr);
                return 0;
            }

            *ppv = IntPtr.Zero;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint HandlerAddRef(IntPtr thisPtr) => AddRefCore(thisPtr);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint HandlerRelease(IntPtr thisPtr) => ReleaseCore(thisPtr);

        private static uint AddRefCore(IntPtr thisPtr)
        {
            var obj = (ButtonHandlerObject*)thisPtr;
            return (uint)Interlocked.Increment(ref obj->RefCount);
        }

        private static uint ReleaseCore(IntPtr thisPtr)
        {
            var obj = (ButtonHandlerObject*)thisPtr;
            int remaining = Interlocked.Decrement(ref obj->RefCount);
            if (remaining == 0)
            {
                if (obj->CallbackHandle != IntPtr.Zero)
                {
                    GCHandle.FromIntPtr(obj->CallbackHandle).Free();
                    obj->CallbackHandle = IntPtr.Zero;
                }

                NativeMemory.Free(obj);
            }

            return (uint)Math.Max(0, remaining);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int HandlerInvoke(IntPtr thisPtr, IntPtr sender, IntPtr args)
        {
            _ = sender;
            if (args == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                var obj = (ButtonHandlerObject*)thisPtr;
                if (obj->CallbackHandle == IntPtr.Zero)
                {
                    return 0;
                }

                var vtbl = *(IntPtr**)args;
                var getButton = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtbl[6];
                int button = -1;
                if (getButton(args, &button) == 0 &&
                    GCHandle.FromIntPtr(obj->CallbackHandle).Target is Action<int> callback)
                {
                    callback(button);
                }
            }
            catch
            {
            }

            return 0;
        }

        private static IntPtr CreateComHandler(Action<int> onButtonPressed)
        {
            var obj = (ButtonHandlerObject*)NativeMemory.AllocZeroed((nuint)sizeof(ButtonHandlerObject));
            obj->Vtable = HandlerVtable;
            obj->RefCount = 1;
            obj->CallbackHandle = GCHandle.ToIntPtr(GCHandle.Alloc(onButtonPressed));
            return (IntPtr)obj;
        }

        public static WindowsSmtcBridge? TryCreate(IntPtr hwnd, Action<int> onButtonPressed)
        {
            if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                return null;
            }

            IntPtr mediaPlayerPtr = IntPtr.Zero;
            IntPtr smtcPtr = IntPtr.Zero;

            // Primary path: WinRT MediaPlayer session (drives Windows 10/11 SMTC overlay reliably on Win32/Avalonia).
            if (TryCreateViaMediaPlayer(out mediaPlayerPtr, out smtcPtr) && smtcPtr != IntPtr.Zero)
            {
                return CreateFromSmtcPointer(mediaPlayerPtr, smtcPtr, onButtonPressed);
            }

            if (mediaPlayerPtr != IntPtr.Zero)
            {
                Marshal.Release(mediaPlayerPtr);
                mediaPlayerPtr = IntPtr.Zero;
            }

            // Fallback path: ISystemMediaTransportControlsInterop::GetForWindow
            const string runtimeClass = "Windows.Media.SystemMediaTransportControls";
            if (WindowsCreateString(runtimeClass, (uint)runtimeClass.Length, out IntPtr hString) != 0)
            {
                return null;
            }

            try
            {
                Guid iidInterop = new("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A");
                if (RoGetActivationFactory(hString, ref iidInterop, out IntPtr interopPtr) != 0 || interopPtr == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var interopVtbl = *(IntPtr**)interopPtr;
                    var getForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)interopVtbl[6];
                    Guid iidSmtc = new("99FA3FF4-1742-42A6-902E-087D41F965EC");
                    int hr = getForWindow(interopPtr, hwnd, &iidSmtc, &smtcPtr);
                    if (hr != 0 || smtcPtr == IntPtr.Zero)
                    {
                        return null;
                    }

                    return CreateFromSmtcPointer(IntPtr.Zero, smtcPtr, onButtonPressed);
                }
                finally
                {
                    Marshal.Release(interopPtr);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                WindowsDeleteString(hString);
            }
        }

        private static bool TryCreateViaMediaPlayer(out IntPtr mediaPlayerPtr, out IntPtr smtcPtr)
        {
            mediaPlayerPtr = IntPtr.Zero;
            smtcPtr = IntPtr.Zero;
            const string mpClass = "Windows.Media.Playback.MediaPlayer";
            if (WindowsCreateString(mpClass, (uint)mpClass.Length, out IntPtr hClass) != 0)
            {
                return false;
            }

            try
            {
                if (RoActivateInstance(hClass, out mediaPlayerPtr) != 0 || mediaPlayerPtr == IntPtr.Zero)
                {
                    return false;
                }

                // Disable MediaPlayer.CommandManager so our manual SMTC ButtonPressed + PlaybackStatus rules apply.
                Guid iidMp3 = new("EE0660DA-031B-4FEB-BD9B-92E0A0A8D299");
                if (Marshal.QueryInterface(mediaPlayerPtr, ref iidMp3, out IntPtr mp3Ptr) == 0 && mp3Ptr != IntPtr.Zero)
                {
                    try
                    {
                        var mp3Vtbl = *(IntPtr**)mp3Ptr;
                        var getCmdMgr = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)mp3Vtbl[17];
                        IntPtr cmdMgrPtr = IntPtr.Zero;
                        if (getCmdMgr(mp3Ptr, &cmdMgrPtr) == 0 && cmdMgrPtr != IntPtr.Zero)
                        {
                            try
                            {
                                var cmdVtbl = *(IntPtr**)cmdMgrPtr;
                                var putEnabled = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)cmdVtbl[7];
                                putEnabled(cmdMgrPtr, 0);
                            }
                            finally
                            {
                                Marshal.Release(cmdMgrPtr);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(mp3Ptr);
                    }
                }

                Guid iidMp2 = new("3C841218-2123-4FC5-9082-2F883F77BDF5");
                if (Marshal.QueryInterface(mediaPlayerPtr, ref iidMp2, out IntPtr mp2Ptr) == 0 && mp2Ptr != IntPtr.Zero)
                {
                    try
                    {
                        var mp2Vtbl = *(IntPtr**)mp2Ptr;
                        var getSmtc = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)mp2Vtbl[6];
                        fixed (IntPtr* pSmtc = &smtcPtr)
                        {
                            return getSmtc(mp2Ptr, pSmtc) == 0 && smtcPtr != IntPtr.Zero;
                        }
                    }
                    finally
                    {
                        Marshal.Release(mp2Ptr);
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                WindowsDeleteString(hClass);
            }
        }

        private static WindowsSmtcBridge CreateFromSmtcPointer(IntPtr mediaPlayerPtr, IntPtr smtcPtr, Action<int> onButtonPressed)
        {
            IntPtr handlerPtr = CreateComHandler(onButtonPressed);
            long token = 0;
            var vtbl = *(IntPtr**)smtcPtr;

            var addButtonPressed = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)vtbl[32];
            if (addButtonPressed(smtcPtr, handlerPtr, &token) != 0)
            {
                ReleaseCore(handlerPtr);
                handlerPtr = IntPtr.Zero;
                token = 0;
            }

            var putBool11 = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtbl[11]; // put_IsEnabled
            var putBool13 = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtbl[13]; // put_IsPlayEnabled
            var putBool15 = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtbl[15]; // put_IsStopEnabled
            var putBool17 = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtbl[17]; // put_IsPauseEnabled
            var putBool25 = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtbl[25]; // put_IsPreviousEnabled
            var putBool27 = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtbl[27]; // put_IsNextEnabled
            var putStatus = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtbl[7];   // put_PlaybackStatus

            putBool11(smtcPtr, 1);
            putBool13(smtcPtr, 1);
            putBool17(smtcPtr, 1);
            putBool15(smtcPtr, 1);
            putBool27(smtcPtr, 1);
            putBool25(smtcPtr, 1);
            putStatus(smtcPtr, 0); // Closed

            return new WindowsSmtcBridge(mediaPlayerPtr, smtcPtr, handlerPtr, token);
        }

        public void Update(AudioTrackItem? track, bool isPlaying)
        {
            if (_disposed || _smtcPtr == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var vtbl = *(IntPtr**)_smtcPtr;
                int status = track is null ? 0 : (isPlaying ? 3 : 4); // Closed : Playing : Paused
                var putIsEnabled = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)vtbl[11];
                var putStatus = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtbl[7];

                string? trackId = track?.Id;
                string? title = track?.Title;
                string? artist = track?.Artist;
                string? album = track?.Album;
                bool trackChanged = !string.Equals(_lastTrackId, trackId, StringComparison.Ordinal) ||
                                    !string.Equals(_lastTitle, title, StringComparison.Ordinal) ||
                                    !string.Equals(_lastArtist, artist, StringComparison.Ordinal) ||
                                    !string.Equals(_lastAlbum, album, StringComparison.Ordinal);

                if (track is null)
                {
                    if (_lastStatus != 0)
                    {
                        _lastStatus = 0;
                        putStatus(_smtcPtr, 0);
                    }

                    if (trackChanged)
                    {
                        _lastTrackId = null;
                        _lastTitle = null;
                        _lastArtist = null;
                        _lastAlbum = null;

                        var getUpdater = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[8];
                        IntPtr updaterPtr = IntPtr.Zero;
                        if (getUpdater(_smtcPtr, &updaterPtr) == 0 && updaterPtr != IntPtr.Zero)
                        {
                            try
                            {
                                var updVtbl = *(IntPtr**)updaterPtr;
                                var clearAll = (delegate* unmanaged[Stdcall]<IntPtr, int>)updVtbl[16];
                                var update = (delegate* unmanaged[Stdcall]<IntPtr, int>)updVtbl[17];
                                clearAll(updaterPtr);
                                update(updaterPtr);
                            }
                            finally
                            {
                                Marshal.Release(updaterPtr);
                            }
                        }

                        putIsEnabled(_smtcPtr, 0);
                    }

                    return;
                }

                // Order matters for Windows SMTC: enable -> DisplayUpdater.Update() -> put_PlaybackStatus.
                putIsEnabled(_smtcPtr, 1);

                if (trackChanged)
                {
                    _lastTrackId = trackId;
                    _lastTitle = title;
                    _lastArtist = artist;
                    _lastAlbum = album;

                    var getUpdater = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[8];
                    IntPtr updaterPtr = IntPtr.Zero;
                    if (getUpdater(_smtcPtr, &updaterPtr) == 0 && updaterPtr != IntPtr.Zero)
                    {
                        try
                        {
                            var updVtbl = *(IntPtr**)updaterPtr;
                            var putType = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)updVtbl[7];
                            putType(updaterPtr, 1); // MediaPlaybackType.Music

                            var getMusicProps = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)updVtbl[12];
                            IntPtr musicPtr = IntPtr.Zero;
                            if (getMusicProps(updaterPtr, &musicPtr) == 0 && musicPtr != IntPtr.Zero)
                            {
                                try
                                {
                                    var musicVtbl = *(IntPtr**)musicPtr;
                                    SetHStringProp(musicPtr, musicVtbl[7], track.Title);   // put_Title
                                    SetHStringProp(musicPtr, musicVtbl[11], track.Artist); // put_Artist
                                    SetHStringProp(musicPtr, musicVtbl[9], track.Album);   // put_AlbumArtist
                                }
                                finally
                                {
                                    Marshal.Release(musicPtr);
                                }
                            }

                            var commitUpdate = (delegate* unmanaged[Stdcall]<IntPtr, int>)updVtbl[17];
                            commitUpdate(updaterPtr);
                        }
                        finally
                        {
                            Marshal.Release(updaterPtr);
                        }
                    }
                }

                if (trackChanged || status != _lastStatus)
                {
                    _lastStatus = status;
                    putStatus(_smtcPtr, status);
                }
            }
            catch
            {
            }
        }

        private static void SetHStringProp(IntPtr comPtr, IntPtr fnPtr, string? value)
        {
            string safe = value ?? string.Empty;
            if (WindowsCreateString(safe, (uint)safe.Length, out IntPtr hStr) != 0)
            {
                return;
            }

            try
            {
                var setter = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)fnPtr;
                setter(comPtr, hStr);
            }
            finally
            {
                WindowsDeleteString(hStr);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_smtcPtr != IntPtr.Zero)
            {
                if (_buttonToken != 0)
                {
                    try
                    {
                        var vtbl = *(IntPtr**)_smtcPtr;
                        var removeButtonPressed = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)vtbl[33];
                        removeButtonPressed(_smtcPtr, _buttonToken);
                    }
                    catch
                    {
                    }
                }

                Marshal.Release(_smtcPtr);
            }

            if (_mediaPlayerPtr != IntPtr.Zero)
            {
                Marshal.Release(_mediaPlayerPtr);
            }

            if (_handlerPtr != IntPtr.Zero)
            {
                ReleaseCore(_handlerPtr);
            }
        }

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);
    }

    private sealed class WindowsGlobalMediaKeyHook : IDisposable
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId;

        private WindowsGlobalMediaKeyHook(LowLevelKeyboardProc proc, IntPtr hookId)
        {
            _proc = proc;
            _hookId = hookId;
        }

        ~WindowsGlobalMediaKeyHook() => Dispose();

        public static WindowsGlobalMediaKeyHook? TryInstall(Action<int> onMediaKey)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                LowLevelKeyboardProc proc = null!;
                proc = (nCode, wParam, lParam) =>
                {
                    try
                    {
                        if (nCode >= 0 && (wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104))
                        {
                            int vkCode = Marshal.ReadInt32(lParam);
                            int? mapped = vkCode switch
                            {
                                0xB3 => -1, // VK_MEDIA_PLAY_PAUSE
                                0xB0 => 6,  // VK_MEDIA_NEXT_TRACK
                                0xB1 => 7,  // VK_MEDIA_PREV_TRACK
                                0xB2 => 2,  // VK_MEDIA_STOP
                                _ => null
                            };

                            if (mapped.HasValue)
                            {
                                onMediaKey(mapped.Value);
                            }
                        }
                    }
                    catch
                    {
                        // Never allow managed exceptions to cross the native Win32 hook boundary.
                    }

                    return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                };

                IntPtr hMod = GetModuleHandleW(null);
                IntPtr hook = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, proc, hMod, 0);
                return hook != IntPtr.Zero ? new WindowsGlobalMediaKeyHook(proc, hook) : null;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            IntPtr hookId = Interlocked.Exchange(ref _hookId, IntPtr.Zero);
            if (hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookId);
            }

            GC.KeepAlive(_proc);
            GC.SuppressFinalize(this);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    }
}

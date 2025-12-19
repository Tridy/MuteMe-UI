using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AsyncAwaitBestPractices;

using AudioControl;

using DeviceControl;

using HidSharp;

using Microsoft.Extensions.Logging;

using Utilities;

namespace MuteMeControl.Services;

public class ConnectedState
{
    private readonly ILogger _logger;

    private bool _previousIsConnected;

    public ConnectedState(ILogger logger)
    {
        _logger = logger;
        _previousIsConnected = false;
    }

    public bool IsConnected
    {
        get;
        set
        {
            field = value;

            if (field != _previousIsConnected)
            {
                string state = field ? "connected" : "not connected";
                _logger.LogInformation("Button is {State} ...", state);

                _previousIsConnected = field;
            }
        }
    }
}

public class Button
{
    private static readonly Lock Lock = new();

    private readonly ConnectedState _isConnected;
    private readonly ILogger _logger;
    private readonly IMicrophone _microphone;
    private readonly IBackgroundQueue _queue;
    private readonly byte[] _readBuffer = new byte[8];
    private CancellationToken _cancellationToken;

    private Stream? _hidStream;
    private bool _isMuted;

    private bool _isTouched;

    // private Task? _uiListeningTask;

    private MuteMeColor _mutedColor = MuteMeColor.Red;

    // private HidDevice? _muteMeDevice;
    private DeviceInfo? _muteMeDeviceInfo;
    private MuteMeColor _unmutedColor = MuteMeColor.Green;

    private Button(IMicrophone microphone, IBackgroundQueue queue, ILogger logger)
    {
        _isConnected = new ConnectedState(logger);
        _microphone = microphone;
        _queue = queue;
        _logger = logger;
    }

    public static Button FromMicrophoneAndQueueAndLogger(IMicrophone microphone, IBackgroundQueue queue, ILogger logger)
    {
        return new Button(microphone, queue, logger);
    }

    public async Task MonitorAsync(IOptionsManager optionsManager, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;

        AssignColorsFromOptions(optionsManager);

        while (!cancellationToken.IsCancellationRequested)
        {
            bool isConnected = ListenIfConnected(cancellationToken);

            if (isConnected)
            {
                await _queue.SendButtonToUiAsync(ButtonToUiWorkItemType.Connected);
            }
            else
            {
                await _queue.SendButtonToUiAsync(ButtonToUiWorkItemType.Disconnected);
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        _isConnected.IsConnected = false;
        _hidStream?.Close();
        _hidStream?.Dispose();
    }

    private void AssignColorsFromOptions(IOptionsManager optionsManager)
    {
        _mutedColor = GetMuteMeColorFromColor(optionsManager.GetOptions().Main.MutedColor);
        _unmutedColor = GetMuteMeColorFromColor(optionsManager.GetOptions().Main.UnmutedColor);
    }

    private static MuteMeColor GetMuteMeColorFromColor(Color color)
    {
        string colorName = color.Name;
        MuteMeColor muteMeColor = (MuteMeColor)Enum.Parse(typeof(MuteMeColor), colorName);
        return muteMeColor;
    }

    public async Task CycleColors(CancellationToken cancellationToken)
    {
        bool connected = ListenIfConnected(cancellationToken);

        if (!connected)
        {
            throw new InvalidOperationException("Could not connect to MuteMe device.");
        }

        MuteMeColor[] colors = new[]
        {
            MuteMeColor.Blue, MuteMeColor.Cyan, MuteMeColor.Green, MuteMeColor.Purple, MuteMeColor.Red, MuteMeColor.White, MuteMeColor.Yellow, MuteMeColor.NoColor
        };

        foreach (MuteMeColor color in colors)
        {
            SetButtonState(color, MuteMeMode.FullBright);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        SetButtonState(MuteMeColor.NoColor, MuteMeMode.FullBright);
    }

    private bool ListenIfConnected(CancellationToken cancellationToken)
    {
        if (_isConnected.IsConnected)
        {
            if (_muteMeDeviceInfo is null)
            {
                return false;
            }

            bool isDeviceStillConnected = Devices.DeviceExists(_muteMeDeviceInfo.VendorId, _muteMeDeviceInfo.ProductId);

            if (!isDeviceStillConnected)
            {
                _isConnected.IsConnected = false;
                _muteMeDeviceInfo = null;
            }

            return true;
        }

        DeviceInfo? deviceInfo = GetConnectedDeviceIfExists();

        if (deviceInfo == null)
        {
            _isConnected.IsConnected = false;
            _muteMeDeviceInfo = null;
            return false;
        }

        DeviceList? usbList = DeviceList.Local;

        HidDevice? muteMeDevice = usbList?.GetHidDeviceOrNull(deviceInfo.VendorId, deviceInfo.ProductId);

        if (muteMeDevice == null)
        {
            _isConnected.IsConnected = false;
            cancellationToken.WaitHandle.WaitOne(5000);
            return false;
        }

        _muteMeDeviceInfo = deviceInfo;

        if (!muteMeDevice.TryOpen(out HidStream hidStream))
        {
            _isConnected.IsConnected = false;
            Debug.WriteLine("Could not establish connection to MuteMe, waiting for 5 seconds before retrying ...");
            cancellationToken.WaitHandle.WaitOne(5000);
            return false;
        }

        ListenToUsb(hidStream);
        ListenToUi(cancellationToken);
        _isConnected.IsConnected = true;
        AdjustForAudioState();

        return true;
    }

    private DeviceInfo? GetConnectedDeviceIfExists()
    {
        if (_muteMeDeviceInfo is null)
        {
            DeviceInfo? deviceInfo = Devices.GetMuteMeDeviceInfo();
            return deviceInfo;
        }

        bool deviceExists = Devices.DeviceExists(_muteMeDeviceInfo.VendorId, _muteMeDeviceInfo.ProductId);

        return deviceExists ? _muteMeDeviceInfo : null;
    }

    private void ListenToUi(CancellationToken cancellationToken)
    {
        Task.Run(async () =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        UiToButtonWorkItem workItem = await _queue.ReceiveUiToButtonAsync(cancellationToken);

                        switch (workItem.Type)
                        {
                            case UiToButtonWorkItemType.MuteColor:
                                _logger.LogInformation("Received MuteColor request: {Color}", workItem.Data);
                                _mutedColor = (MuteMeColor)Enum.Parse(typeof(MuteMeColor), workItem.Data);
                                RefreshButtonColors();
                                break;
                            case UiToButtonWorkItemType.UnmuteColor:
                                _logger.LogInformation("Received UnmuteColor request: {Color}", workItem.Data);
                                _unmutedColor = (MuteMeColor)Enum.Parse(typeof(MuteMeColor), workItem.Data);
                                RefreshButtonColors();
                                break;
                            case UiToButtonWorkItemType.ShuttingDown:
                                _logger.LogInformation("Received OnShutdown request: {Data}", workItem.Data);
                                TurnOffColors();
                                break;
                            case UiToButtonWorkItemType.Mode:
                                _logger.LogInformation("Received Mode request: {Mode}", workItem.Data);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException($"'{workItem.Type}' is not supported type.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation, no need to handle
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in UI listener loop");
                }
            }, cancellationToken)
            .SafeFireAndForget(onException: ex => _logger.LogError(ex, $"Error in {nameof(Button)} when calling {nameof(ListenToUi)}"));
    }

    private void TurnOffColors()
    {
        _mutedColor = MuteMeColor.NoColor;
        _unmutedColor = MuteMeColor.NoColor;
        RefreshButtonColors();
    }

    private void AdjustForAudioState()
    {
        bool isAudioMuted = _microphone.IsMuted();

        if (isAudioMuted)
        {
            SetButtonState(_mutedColor, MuteMeMode.FullBright);
            _isMuted = true;
            _queue.SendButtonToUiAsync(ButtonToUiWorkItemType.Muted)
                .SafeFireAndForget(onException: ex => _logger.LogError(ex, $"Error in {nameof(Button)} when calling {nameof(AdjustForAudioState)} for state Muted"));
        }
        else
        {
            SetButtonState(_unmutedColor, MuteMeMode.FullBright);
            _isMuted = false;
            _queue.SendButtonToUiAsync(ButtonToUiWorkItemType.Unmuted)
                .SafeFireAndForget(onException: ex => _logger.LogError(ex, $"Error in {nameof(Button)} when calling {nameof(AdjustForAudioState)} for state Unmuted"));
        }
    }

    private void ListenToUsb(HidStream hidStream)
    {
        _hidStream?.Dispose();
        _hidStream = Stream.Synchronized(hidStream);
        _hidStream.WriteTimeout = 100;
        _hidStream.ReadTimeout = 100;

        _ = _hidStream.BeginRead(_readBuffer, 0, 8, OnUsbDataReceived, null);
    }

    private void RefreshButtonColors()
    {
        if (_isMuted)
        {
            SetButtonState(_mutedColor, MuteMeMode.FullBright);
            return;
        }

        if (!_isMuted)
        {
            SetButtonState(_unmutedColor, MuteMeMode.FullBright);
        }
    }

    private void SetButtonState(MuteMeColor color, MuteMeMode mode)
    {
        if (!_isConnected.IsConnected)
        {
            return;
        }

        try
        {
            byte command = (byte)((byte)color + (byte)mode);

            lock (Lock)
            {
                byte[] buffer = new byte[]
                {
                    0, command
                };

                _hidStream?.Write(buffer, 0, 2);
            }
        }
        catch (Exception ex)
        {
            _isConnected.IsConnected = false;
            _logger.LogError(ex, ex.Message);
        }
    }

    private void OnUsbDataReceived(IAsyncResult result)
    {
        lock (Lock)
        {
            switch (_readBuffer[4])
            {
                case 0:
                    break;

                case 1:
                    if (!_isTouched)
                    {
                        OnTouched();
                    }

                    break;

                case 2:
                    if (_isTouched)
                    {
                        OnUntouched();
                    }

                    break;

                default:
                    _logger.LogTrace("Unknown value from device: " + _readBuffer[4]);
                    break;
            }
        }

        // Console.WriteLine("Queue: " +  _queueEntry.Count);

        if (!_cancellationToken.IsCancellationRequested)
        {
            _ = _hidStream?.BeginRead(_readBuffer, 0, 8, OnUsbDataReceived, null);
        }
    }

    private void OnUntouched()
    {
        _isTouched = false;
        ToggleState();
    }

    private void OnTouched()
    {
        _isTouched = true;
    }

    private void ToggleState()
    {
        if (_isMuted)
        {
            _logger.LogInformation("Unmuting");
            SetButtonState(_unmutedColor, MuteMeMode.FullBright);
            _microphone.Unmute();
            _isMuted = false;
            NotifyUiOnMutedState();
            return;
        }

        _logger.LogInformation("Muting");
        SetButtonState(_mutedColor, MuteMeMode.FullBright);
        _microphone.Mute();
        _isMuted = true;
        NotifyUiOnMutedState();
    }

    private void NotifyUiOnMutedState()
    {
        _queue.SendButtonToUiAsync(_isMuted ? ButtonToUiWorkItemType.Muted : ButtonToUiWorkItemType.Unmuted)
            .SafeFireAndForget(onException: ex => _logger.LogError(ex, $"Error in {nameof(Button)} when calling {nameof(NotifyUiOnMutedState)}"));
    }
}
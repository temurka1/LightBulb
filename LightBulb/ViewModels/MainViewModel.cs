﻿using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.CommandWpf;
using GalaSoft.MvvmLight.Threading;
using LightBulb.Models;
using LightBulb.Services;
using NegativeLayer.Extensions;
using NegativeLayer.WPFExtensions;

namespace LightBulb.ViewModels
{
    public class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly TemperatureService _temperatureService;
        private readonly GammaControlService _gammaControlService;
        private readonly WindowService _windowService;
        private readonly GeolocationService _geolocationService;

        private readonly ValueSmoother _smoother;

        private readonly SyncedTimer _temperatureUpdateTimer;
        private readonly Timer _pollingTimer;
        private readonly Timer _disableTemporarilyTimer;
        private readonly Timer _cyclePreviewTimer;
        private readonly SyncedTimer _internetSyncTimer;

        private bool _isEnabled;
        private bool _isBlocked;
        private bool _isPreviewModeEnabled;
        private string _statusText;
        private CycleState _cycleState;
        private DateTime _time;
        private DateTime _previewTime;
        private ushort _temperature;
        private ushort _previewTemperature;

        public Settings Settings => Settings.Default;

        /// <summary>
        /// Enables or disables the program
        /// </summary>
        public bool IsEnabled
        {
            get { return _isEnabled; }
            set
            {
                if (!Set(ref _isEnabled, value)) return;
                if (value) _disableTemporarilyTimer.IsEnabled = false;
                if (value && !IsPreviewModeEnabled) UpdateTemperature();

                _temperatureUpdateTimer.IsEnabled = value;
                _pollingTimer.IsEnabled = value && !IsBlocked && Settings.IsGammaPollingEnabled;

                UpdateTemperature();
                UpdateGamma();
                UpdateStatus();
            }
        }

        /// <summary>
        /// Whether gamma control is blocked by something 
        /// </summary>
        public bool IsBlocked
        {
            get { return _isBlocked; }
            private set
            {
                if (!Set(ref _isBlocked, value)) return;

                _pollingTimer.IsEnabled = !value && IsEnabled && Settings.IsGammaPollingEnabled;

                UpdateTemperature();
                UpdateGamma();
                UpdateStatus();
            }
        }

        /// <summary>
        /// Enables or disables the preview mode
        /// </summary>
        public bool IsPreviewModeEnabled
        {
            get { return _isPreviewModeEnabled; }
            set
            {
                if (!Set(ref _isPreviewModeEnabled, value)) return;
                
                UpdateGamma();
                UpdateStatus();
            }
        }

        /// <summary>
        /// Current status text
        /// </summary>
        public string StatusText
        {
            get { return _statusText; }
            private set { Set(ref _statusText, value); }
        }

        /// <summary>
        /// State of the current cycle
        /// </summary>
        public CycleState CycleState
        {
            get { return _cycleState; }
            private set { Set(ref _cycleState, value); }
        }

        /// <summary>
        /// Time used for temperature calculations
        /// </summary>
        public DateTime Time
        {
            get { return _time; }
            private set
            {
                if (!Set(ref _time, value)) return;

                UpdateStatus();
            }
        }

        /// <summary>
        /// Time used for preview-mode temperature calculations
        /// </summary>
        public DateTime PreviewTime
        {
            get { return _previewTime; }
            private set
            {
                if (!Set(ref _previewTime, value)) return;

                UpdateStatus();
            }
        }

        /// <summary>
        /// Current light temperature
        /// </summary>
        public ushort Temperature
        {
            get { return _temperature; }
            private set
            {
                if (!Set(ref _temperature, value)) return;

                UpdateGamma();
                UpdateStatus();
            }
        }

        /// <summary>
        /// Preview mode light temperature
        /// </summary>
        public ushort PreviewTemperature
        {
            get { return _previewTemperature; }
            set
            {
                if (!Set(ref _previewTemperature, value)) return;
                if (!IsPreviewModeEnabled) return;

                UpdateGamma();
                UpdateStatus();
            }
        }

        // Commands
        public RelayCommand ShowMainWindowCommand { get; }
        public RelayCommand ExitApplicationCommand { get; }
        public RelayCommand AboutCommand { get; }
        public RelayCommand ToggleEnabledCommand { get; }
        public RelayCommand<double> DisableTemporarilyCommand { get; }
        public RelayCommand RestoreOriginalCommand { get; }
        public RelayCommand RestoreDefaultCommand { get; }
        public RelayCommand StartCyclePreviewCommand { get; }

        public MainViewModel(TemperatureService temperatureService, GammaControlService gammaControlService,
            WindowService windowService, GeolocationService geolocationService)
        {
            // Services
            _temperatureService = temperatureService;
            _gammaControlService = gammaControlService;
            _windowService = windowService;
            _geolocationService = geolocationService;
            _smoother = new ValueSmoother();

            _windowService.FullScreenStateChanged += (sender, args) => UpdateBlockStatus();

            // Timers
            _temperatureUpdateTimer = new SyncedTimer();
            _temperatureUpdateTimer.Tick += (sender, args) => UpdateTemperature();
            _pollingTimer = new Timer();
            _pollingTimer.Tick += (sender, args) => UpdateGamma();
            _cyclePreviewTimer = new Timer(TimeSpan.FromMilliseconds(10));
            _cyclePreviewTimer.Tick += (sender, args) => CyclePreviewUpdateTemperature();
            _disableTemporarilyTimer = new Timer();
            _disableTemporarilyTimer.Tick += (sender, args) => IsEnabled = true;
            _internetSyncTimer = new SyncedTimer();
            _internetSyncTimer.Tick += async (sender, args) => await InternetSyncAsync();

            // Settings
            Settings.PropertyChanged += (sender, args) => UpdateConfiguration();
            Settings.PropertyChanged += (sender, args) =>
            {
                // Refresh temperature
                if (args.PropertyName.IsEither(nameof(Settings.TemperatureEpsilon), nameof(Settings.MinTemperature),
                        nameof(Settings.MaxTemperature), nameof(Settings.TemperatureSwitchDuration),
                        nameof(Settings.SunriseTime), nameof(Settings.SunsetTime)))
                {
                    UpdateTemperature();
                }

                // Refresh geo-info
                if (args.PropertyName == nameof(Settings.IsInternetTimeSyncEnabled) &&
                    Settings.IsInternetTimeSyncEnabled)
                {
                    InternetSyncAsync().Forget();
                }
            };

            // Commands
            ShowMainWindowCommand = new RelayCommand(() =>
            {
                Application.Current.MainWindow.Show();
                Application.Current.MainWindow.Activate();
                Application.Current.MainWindow.Focus();
            });
            ExitApplicationCommand = new RelayCommand(() => Application.Current.ShutdownSafe());
            AboutCommand = new RelayCommand(() => Process.Start("https://github.com/Tyrrrz/LightBulb"));
            ToggleEnabledCommand = new RelayCommand(() => IsEnabled = !IsEnabled);
            DisableTemporarilyCommand = new RelayCommand<double>(DisableTemporarily);
            RestoreOriginalCommand = new RelayCommand(() => _gammaControlService.RestoreOriginal());
            RestoreDefaultCommand = new RelayCommand(() => _gammaControlService.RestoreDefault());
            StartCyclePreviewCommand = new RelayCommand(StartCyclePreview, () => !_cyclePreviewTimer.IsEnabled);

            // Init
            Temperature = Settings.DefaultMonitorTemperature;
            UpdateConfiguration();
            if (Settings.IsInternetTimeSyncEnabled)
                InternetSyncAsync().Forget();
            IsEnabled = true;
        }

        private void UpdateConfiguration()
        {
            // Services
            _windowService.UseEventHooks = Settings.IsFullscreenBlocking;

            // Timers
            _temperatureUpdateTimer.Interval = Settings.TemperatureUpdateInterval;
            _pollingTimer.Interval = Settings.GammaPollingInterval;
            _internetSyncTimer.Interval = Settings.InternetSyncInterval;

            _pollingTimer.IsEnabled = IsEnabled && Settings.IsGammaPollingEnabled;
            _internetSyncTimer.IsEnabled = Settings.IsInternetTimeSyncEnabled;

            Debug.WriteLine("Updated configuration", GetType().Name);
        }

        private void UpdateBlockStatus()
        {
            IsBlocked = Settings.IsFullscreenBlocking && _windowService.IsForegroundFullScreen;

            Debug.WriteLine($"Updated block status (to {IsBlocked})", GetType().Name);
        }

        private void UpdateStatus()
        {
            // Preview mode (not 24hr cycle preview)
            if (IsPreviewModeEnabled && !_cyclePreviewTimer.IsEnabled)
            {
                CycleState = CycleState.Disabled;
                StatusText = $"Temp: {PreviewTemperature}K   (preview)";
            }
            // Preview mode (24 hr cycle preview)
            else if (IsPreviewModeEnabled && _cyclePreviewTimer.IsEnabled)
            {
                if (PreviewTemperature >= Settings.MaxTemperature) CycleState = CycleState.Day;
                else if (PreviewTemperature <= Settings.MinTemperature) CycleState = CycleState.Night;
                else CycleState = CycleState.Transition;

                StatusText = $"Temp: {PreviewTemperature}K   Time: {PreviewTime:t}   (preview)";
            }
            // Not enabled
            else if (!IsEnabled)
            {
                CycleState = CycleState.Disabled;
                StatusText = "LightBulb is off";
            }
            // Blocked
            else if (IsBlocked)
            {
                CycleState = CycleState.Disabled;
                StatusText = "LightBulb is blocked";
            }
            // Realtime mode
            else
            {
                if (Temperature >= Settings.MaxTemperature) CycleState = CycleState.Day;
                else if (Temperature <= Settings.MinTemperature) CycleState = CycleState.Night;
                else CycleState = CycleState.Transition;

                StatusText = $"Temp: {Temperature}K   Time: {Time:t}";
            }
        }

        private void UpdateGamma()
        {
            ushort temp = IsPreviewModeEnabled ? PreviewTemperature : Temperature;
            var intensity = ColorIntensity.FromTemperature(temp);
            _gammaControlService.SetDisplayGammaLinear(intensity);

            Debug.WriteLine($"Set gamma (intensity to {intensity})", GetType().Name);
        }

        private void UpdateTemperature()
        {
            Time = DateTime.Now;
            ushort currentTemp = Temperature;
            ushort newTemp = IsEnabled && !IsBlocked
                ? _temperatureService.GetTemperature(Time)
                : Settings.DefaultMonitorTemperature;
            int diff = Math.Abs(currentTemp - newTemp);

            // Don't update if difference is too small, unless it's either max or min temperature
            if (!newTemp.IsEither(Settings.MinTemperature, Settings.MaxTemperature) &&
                diff < Settings.TemperatureEpsilon) return;

            // Smooth transition
            if (Settings.IsTemperatureSmoothingEnabled && diff >= Settings.MinimumSmoothingTemperature)
            {
                _smoother.Set(currentTemp, newTemp, temp => Temperature = (ushort) temp, Settings.TemperatureSmoothingDuration);

                Debug.WriteLine($"Started smooth temperature transition (to {Temperature})", GetType().Name);
            }
            // Instant transition
            else
            {
                _smoother.Stop();
                Temperature = newTemp;

                Debug.WriteLine($"Updated temperature (to {Temperature})", GetType().Name);
            }
        }

        private void CyclePreviewUpdateTemperature()
        {
            PreviewTime = PreviewTime.AddHours(0.05);
            ushort currentTemp = PreviewTemperature;
            ushort newTemp = _temperatureService.GetTemperature(PreviewTime);
            int diff = Math.Abs(currentTemp - newTemp);

            // Don't update if difference is too small, unless it's either max or min temperature
            if (!newTemp.IsEither(Settings.MinTemperature, Settings.MaxTemperature) &&
                diff < Settings.TemperatureEpsilon) return;

            PreviewTemperature = newTemp;
            IsPreviewModeEnabled = true;

            // Ending condition
            if ((PreviewTime - Time).TotalHours >= 24)
            {
                _cyclePreviewTimer.IsEnabled = false;
                IsPreviewModeEnabled = false;
                DispatcherHelper.CheckBeginInvokeOnUI(() => StartCyclePreviewCommand.RaiseCanExecuteChanged());
            }
        }

        private void StartCyclePreview()
        {
            PreviewTime = Time;
            _cyclePreviewTimer.IsEnabled = true;

            Debug.WriteLine("Started cycle preview", GetType().Name);
        }

        private void DisableTemporarily(double ms)
        {
            _disableTemporarilyTimer.IsEnabled = false;
            _disableTemporarilyTimer.Interval = TimeSpan.FromMilliseconds(ms);
            _disableTemporarilyTimer.IsEnabled = true;
            IsEnabled = false;
        }

        private async Task InternetSyncAsync()
        {
            Debug.WriteLine("Start internet sync", GetType().Name);

            // Get coordinates
            var geoinfo = await _geolocationService.GetGeolocationInfoAsync();
            if (geoinfo == null) return; // fail
            Settings.GeoInfo = geoinfo;

            // Get the sunrise/sunset times
            var solarInfo = await _geolocationService.GetSolarInfoAsync(Settings.GeoInfo);
            if (solarInfo == null) return; // fail

            // Update settings
            if (Settings.IsInternetTimeSyncEnabled)
            {
                Settings.SunriseTime = solarInfo.Sunrise.TimeOfDay;
                Settings.SunsetTime = solarInfo.Sunset.TimeOfDay;

                Debug.WriteLine("Solar info updated", GetType().Name);
            }

            Debug.WriteLine("End internet sync", GetType().Name);
        }

        public void Dispose()
        {
            _temperatureUpdateTimer.Dispose();
            _pollingTimer.Dispose();
            _cyclePreviewTimer.Dispose();
            _disableTemporarilyTimer.Dispose();
            _internetSyncTimer.Dispose();
        }
    }
}
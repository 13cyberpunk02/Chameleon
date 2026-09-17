using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Chameleon.Gui.Settings;
using Chameleon.Tunnel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Chameleon.Gui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppSettings _settings = AppSettings.Load();
    private TunnelService? _service;
    private CancellationTokenSource? _cts;

    public MainWindowViewModel()
    {
        Server = _settings.Server;
        ServerKey = _settings.ServerPublicKeyHex;
        Sni = _settings.Sni;
        Socks = _settings.Socks;
        Tun2SocksPath = _settings.Tun2SocksPath;
        LogLevel = _settings.LogLevel;
    }

    [ObservableProperty] private string _server = "";
    [ObservableProperty] private string _serverKey = "";
    [ObservableProperty] private string _sni = "";
    [ObservableProperty] private string _socks = "";
    [ObservableProperty] private string _tun2SocksPath = "";
    [ObservableProperty] private string _logLevel = "error";

    [ObservableProperty] private string _status = "Отключено";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;

    public ObservableCollection<string> Log { get; } = new();

    private bool CanConnect => !IsBusy && !IsConnected;
    private bool CanDisconnect => !IsBusy && IsConnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        SaveSettings();
        IsBusy = true;
        AppendLog("Подключение…");
        UpdateCommands();
        try
        {
            _service = new TunnelService();
            _service.Log += (_, m) => AppendLog(m);
            _service.StatusChanged += (_, s) => Status = Localize(s);

            var options = new TunnelOptions
            {
                Server = Server.Trim(),
                ServerPublicKeyHex = ServerKey.Trim(),
                Sni = string.IsNullOrWhiteSpace(Sni) ? "www.example-cdn.com" : Sni.Trim(),
                SocksListen = string.IsNullOrWhiteSpace(Socks) ? "127.0.0.1:1080" : Socks.Trim(),
                Tun2SocksPath = string.IsNullOrWhiteSpace(Tun2SocksPath)
                    ? (OperatingSystem.IsWindows() ? "tun2socks.exe" : "tun2socks")
                    : Tun2SocksPath.Trim(),
                Tun2SocksLogLevel = string.IsNullOrWhiteSpace(LogLevel) ? "error" : LogLevel.Trim(),
            };

            _cts = new CancellationTokenSource();
            await _service.ConnectAsync(options, _cts.Token);
            IsConnected = true;
            AppendLog("Готово: весь трафик идёт через туннель.");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка: " + ex.Message);
            Status = "Ошибка";
            await SafeDisconnectAsync();
        }
        finally
        {
            IsBusy = false;
            UpdateCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        AppendLog("Отключение…");
        UpdateCommands();
        await SafeDisconnectAsync();
        IsBusy = false;
        UpdateCommands();
    }

    private async Task SafeDisconnectAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_service is not null) await _service.DisconnectAsync();
        }
        catch
        {
            // ignored
        }

        _service = null;
        _cts = null;
        IsConnected = false;
        if (Status != "Ошибка") Status = "Отключено";
    }

    private void UpdateCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    private void AppendLog(string message)
    {
        // из фонового потока — в UI-поток
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
            while (Log.Count > 500) Log.RemoveAt(0);
        });
    }

    private void SaveSettings()
    {
        _settings.Server = Server;
        _settings.ServerPublicKeyHex = ServerKey;
        _settings.Sni = Sni;
        _settings.Socks = Socks;
        _settings.Tun2SocksPath = Tun2SocksPath;
        _settings.LogLevel = LogLevel;
        _settings.Save();
    }

    private static string Localize(TunnelStatus s) => s switch
    {
        TunnelStatus.Connecting => "Подключение…",
        TunnelStatus.Connected => "Подключено",
        TunnelStatus.Error => "Ошибка",
        _ => "Отключено",
    };

    partial void OnIsBusyChanged(bool value) => UpdateCommands();
    partial void OnIsConnectedChanged(bool value) => UpdateCommands();
}
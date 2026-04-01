//App.xaml.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MusicPlayer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\MusicPlayer.SingleInstance";
    private const string SingleInstancePipeName = "MusicPlayer.SingleInstancePipe";

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _pipeCts;

    public App()
    {
        // UI thread exceptions (includes XAML/runtime binding issues that bubble up)
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.ToString(), "Unhandled UI Exception");
            e.Handled = true;
        };

        // Non-UI thread exceptions
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            MessageBox.Show(e.ExceptionObject?.ToString() ?? "Unknown", "Unhandled Exception");
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        bool createdNew;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out createdNew);

        // Another instance is already running: forward any files to it, then exit.
        if (!createdNew)
        {
            if (e.Args != null && e.Args.Length > 0)
                TrySendArgsToRunningInstance(e.Args);

            Shutdown();
            return;
        }

        base.OnStartup(e);

        _pipeCts = new CancellationTokenSource();
        StartPipeServer(_pipeCts.Token);

        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        // Handle files passed by Explorer / drag-to-EXE / multi-select open
        if (e.Args != null && e.Args.Length > 0)
        {
            main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                main.OpenFromShell(e.Args);
            }));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _pipeCts?.Cancel(); } catch { }
        try { _pipeCts?.Dispose(); } catch { }

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // ignore
        }

        try { _singleInstanceMutex?.Dispose(); } catch { }

        base.OnExit(e);
    }

    private void StartPipeServer(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        SingleInstancePipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    using var reader = new BinaryReader(server);

                    int count = reader.ReadInt32();
                    var files = new List<string>(Math.Max(0, count));

                    for (int i = 0; i < count; i++)
                    {
                        string? s = reader.ReadString();
                        if (!string.IsNullOrWhiteSpace(s))
                            files.Add(s);
                    }

                    if (files.Count == 0)
                        continue;

                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (MainWindow is MainWindow mw)
                        {
                            if (mw.WindowState == WindowState.Minimized)
                                mw.WindowState = WindowState.Normal;

                            mw.Show();
                            mw.Topmost = true;
                            mw.Activate();
                            mw.Topmost = false;
                            mw.Focus();

                            mw.OpenFromShell(files);
                        }
                    }));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // keep server alive
                    await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    private static void TrySendArgsToRunningInstance(IEnumerable<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out);
            client.Connect(1200);

            using var writer = new BinaryWriter(client);
            var list = new List<string>();

            foreach (var arg in args)
            {
                if (!string.IsNullOrWhiteSpace(arg))
                    list.Add(arg);
            }

            writer.Write(list.Count);
            foreach (var item in list)
                writer.Write(item);

            writer.Flush();
        }
        catch
        {
            // If forwarding fails, just let the second instance exit quietly.
        }
    }
}
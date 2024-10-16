﻿using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Win32;

using Bloxstrap.Models.SettingTasks.Base;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Bloxstrap
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public const string ProjectName = "Bloxstrap";
        public const string ProjectRepository = "pizzaboxer/bloxstrap";

        public const string RobloxPlayerAppName = "RobloxPlayerBeta";
        public const string RobloxStudioAppName = "RobloxStudioBeta";

        // simple shorthand for extremely frequently used and long string - this goes under HKCU
        public const string UninstallKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProjectName}";

        public static LaunchSettings LaunchSettings { get; private set; } = null!;

        public static BuildMetadataAttribute BuildMetadata = Assembly.GetExecutingAssembly().GetCustomAttribute<BuildMetadataAttribute>()!;

        public static string Version = Assembly.GetExecutingAssembly().GetName().Version!.ToString()[..^2];

        public static readonly MD5 MD5Provider = MD5.Create();

        public static NotifyIconWrapper? NotifyIcon { get; set; }

        public static readonly Logger Logger = new();

        public static readonly Dictionary<string, BaseTask> PendingSettingTasks = new();

        public static readonly JsonManager<Settings> Settings = new();

        public static readonly JsonManager<State> State = new();

        public static readonly FastFlagManager FastFlags = new();

        public static readonly HttpClient HttpClient = new(
            new HttpClientLoggingHandler(
                new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }
            )
        );

        private static bool _showingExceptionDialog = false;

        public static void Terminate(ErrorCode exitCode = ErrorCode.ERROR_SUCCESS)
        {
            int exitCodeNum = (int)exitCode;

            Logger.WriteLine("App::Terminate", $"Terminating with exit code {exitCodeNum} ({exitCode})");

            NotifyIcon?.Dispose();

            Environment.Exit(exitCodeNum);
        }

        void GlobalExceptionHandler(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;

            Logger.WriteLine("App::GlobalExceptionHandler", "An exception occurred");

            FinalizeExceptionHandling(e.Exception);
        }

        public static void FinalizeExceptionHandling(Exception exception, bool log = true)
        {
            if (log)
                Logger.WriteException("App::FinalizeExceptionHandling", exception);

            if (_showingExceptionDialog)
                return;

            _showingExceptionDialog = true;

            if (!LaunchSettings.IsQuiet)
                Frontend.ShowExceptionDialog(exception);

            Terminate(ErrorCode.ERROR_INSTALL_FAILURE);

        }

        private void StartupFinished()
        {
            const string LOG_IDENT = "App::StartupFinished";

            Logger.WriteLine(LOG_IDENT, "Successfully reached end of main thread. Terminating...");

            Terminate();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            const string LOG_IDENT = "App::OnStartup";

            Locale.Initialize();

            base.OnStartup(e);

            Logger.WriteLine(LOG_IDENT, $"Starting {ProjectName} v{Version}");

            if (String.IsNullOrEmpty(BuildMetadata.CommitHash))
                Logger.WriteLine(LOG_IDENT, $"Compiled {BuildMetadata.Timestamp.ToFriendlyString()} from {BuildMetadata.Machine}");
            else
                Logger.WriteLine(LOG_IDENT, $"Compiled {BuildMetadata.Timestamp.ToFriendlyString()} from commit {BuildMetadata.CommitHash} ({BuildMetadata.CommitRef})");

            Logger.WriteLine(LOG_IDENT, $"Loaded from {Paths.Process}");

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            HttpClient.Timeout = TimeSpan.FromSeconds(30);
            HttpClient.DefaultRequestHeaders.Add("User-Agent", ProjectRepository);

            LaunchSettings = new LaunchSettings(e.Args);

            // installation check begins here
            using var uninstallKey = Registry.CurrentUser.OpenSubKey(UninstallKey);
            string? installLocation = null;
            bool fixInstallLocation = false;

            if (uninstallKey?.GetValue("InstallLocation") is string value)
            {
                if (Directory.Exists(value))
                {
                    installLocation = value;
                }
                else
                {
                    // check if user profile folder has been renamed
                    // honestly, i'll be expecting bugs from this
                    var match = Regex.Match(value, @"^[a-zA-Z]:\\Users\\([^\\]+)", RegexOptions.IgnoreCase);

                    if (match.Success)
                    {
                        string newLocation = value.Replace(match.Value, Paths.UserProfile, StringComparison.InvariantCultureIgnoreCase);

                        if (Directory.Exists(newLocation))
                        {
                            installLocation = newLocation;
                            fixInstallLocation = true;
                        }
                    }
                }
            }

            // silently change install location if we detect a portable run
            if (installLocation is null && Directory.GetParent(Paths.Process)?.FullName is string processDir)
            {
                var files = Directory.GetFiles(processDir).Select(x => Path.GetFileName(x)).ToArray();

                // check if settings.json and state.json are the only files in the folder
                if (files.Length <= 3 && files.Contains("Settings.json") && files.Contains("State.json"))
                {
                    installLocation = processDir;
                    fixInstallLocation = true;
                }
            }

            if (installLocation is null)
            {
                Logger.Initialize(true);
                LaunchHandler.LaunchInstaller();
            }
            else
            {
                if (fixInstallLocation)
                {
                    var installer = new Installer
                    {
                        InstallLocation = installLocation,
                        IsImplicitInstall = true
                    };

                    if (installer.CheckInstallLocation())
                    {
                        Logger.WriteLine(LOG_IDENT, $"Changing install location to '{installLocation}'");
                        installer.DoInstall();
                    }
                }

                Paths.Initialize(installLocation);

                // ensure executable is in the install directory
                if (Paths.Process != Paths.Application && !File.Exists(Paths.Application))
                    File.Copy(Paths.Process, Paths.Application);

                Logger.Initialize(LaunchSettings.IsUninstall);

                if (!Logger.Initialized && !Logger.NoWriteMode)
                {
                    Logger.WriteLine(LOG_IDENT, "Possible duplicate launch detected, terminating.");
                    Terminate();
                }

                Settings.Load();
                State.Load();
                FastFlags.Load();

                // we can only parse them now as settings need
                // to be loaded first to know what our channel is
                LaunchSettings.ParseRoblox();

                if (!Locale.SupportedLocales.ContainsKey(Settings.Prop.Locale))
                {
                    Settings.Prop.Locale = "nil";
                    Settings.Save();
                }

                Locale.Set(Settings.Prop.Locale);

                if (!LaunchSettings.IsUninstall)
                    Installer.HandleUpgrade();

                LaunchHandler.ProcessLaunchArgs();
            }

            // Terminate();


            if (!LaunchSettings.IsUninstall && !LaunchSettings.IsMenuLaunch)
                NotifyIcon = new();

            /* #if !DEBUG
                        if (!LaunchSettings.IsUninstall && !IsFirstRun)
                            InstallChecker.CheckUpgrade();
            #endif */

            if (LaunchSettings.IsMenuLaunch)
            {
                Process? menuProcess = Utilities.GetProcessesSafe().Where(x => x.MainWindowTitle == $"{ProjectName} Menu").FirstOrDefault();

                if (menuProcess is not null)
                {
                    var handle = menuProcess.MainWindowHandle;
                    Logger.WriteLine(LOG_IDENT, $"Found an already existing menu window with handle {handle}");
                    PInvoke.SetForegroundWindow((HWND)handle);
                }
                else
                {
                    bool showAlreadyRunningWarning = Process.GetProcessesByName(ProjectName).Length > 1 && !LaunchSettings.IsQuiet;
                    if (showAlreadyRunningWarning) { Frontend.ShowMessageBox("showAlreadyRunningWarning"); }
                }

                StartupFinished();
                return;
            }

            //if (true)
            //ShouldSaveConfigs = true;

            // start bootstrapper and show the bootstrapper modal if we're not running silently
            Logger.WriteLine(LOG_IDENT, "Initializing bootstrapper");
            Bootstrapper bootstrapper = new(LaunchSettings.RobloxLaunchArgs, LaunchSettings.RobloxLaunchMode, installWebView2: false);
            IBootstrapperDialog? dialog = null;

            if (!LaunchSettings.IsQuiet)
            {
                Logger.WriteLine(LOG_IDENT, "Initializing bootstrapper dialog");
                dialog = Settings.Prop.BootstrapperStyle.GetNew();
                bootstrapper.Dialog = dialog;
                dialog.Bootstrapper = bootstrapper;
            }

            // handle roblox singleton mutex for multi-instance launching
            // note we're handling it here in the main thread and NOT in the
            // bootstrapper as handling mutexes in async contexts suuuuuucks

            Mutex? singletonMutex = null;

            if (true && LaunchSettings.RobloxLaunchMode == LaunchMode.Player) // wanted to use Settings.Prop.MultiInstanceLaunching, but hey they just redid the settings page so thats awesome :) (replaced with a true statement)
            {
                Logger.WriteLine(LOG_IDENT, "Creating singleton mutex");

                try
                {
                    Mutex.OpenExisting("ROBLOX_singletonMutex");
                    Logger.WriteLine(LOG_IDENT, "Warning - singleton mutex already exists!");
                }
                catch
                {
                    // create the singleton mutex before the game client does
                    singletonMutex = new Mutex(true, "ROBLOX_singletonMutex");
                }
            }

            Task bootstrapperTask = Task.Run(async () => await bootstrapper.Run()).ContinueWith(t =>
            {
                Logger.WriteLine(LOG_IDENT, "Bootstrapper task has finished");

                // notifyicon is blocking main thread, must be disposed here
                NotifyIcon?.Dispose();

                if (t.IsFaulted)
                    Logger.WriteLine(LOG_IDENT, "An exception occurred when running the bootstrapper");

                if (t.Exception is null)
                    return;

                Logger.WriteException(LOG_IDENT, t.Exception);

                Exception exception = t.Exception;

#if !DEBUG
                if (t.Exception.GetType().ToString() == "System.AggregateException")
                    exception = t.Exception.InnerException!;
#endif

                FinalizeExceptionHandling(exception, false);
            });

            // this ordering is very important as all wpf windows are shown as modal dialogs, mess it up and you'll end up blocking input to one of them
            dialog?.ShowBootstrapper();

            if (!LaunchSettings.IsNoLaunch && Settings.Prop.EnableActivityTracking)
                NotifyIcon?.InitializeContextMenu();

            Logger.WriteLine(LOG_IDENT, "Waiting for bootstrapper task to finish");

            bootstrapperTask.Wait();

            if (singletonMutex is not null)
            {
                Logger.WriteLine(LOG_IDENT, "We have singleton mutex ownership! Running in background until all Roblox processes are closed");

                // we've got ownership of the roblox singleton mutex!
                // if we stop running, everything will screw up once any more roblox instances launched
                while (Process.GetProcessesByName("RobloxPlayerBeta").Any())
                    Thread.Sleep(5000);
            }

            StartupFinished();
        }
    }
}

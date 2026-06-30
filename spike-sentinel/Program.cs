using ForgeSentinel;

// Legacy-safe console setup: only flips to UTF-8 on Windows 10 1909+ (older
// ConHost breaks find.exe/more under code page 65001).
PlatformConsole.Init();

// Tamper guard. No-op unless built with -p:Hardened=true (FORGE_HARDENED); never
// reacts at the detection site — it silently taints integrity for a late, subtle
// failure. See IntegrityGuard.cs.
IntegrityGuard.Arm();

// Register AUMID so Windows allows toast notifications from this unpackaged app
ToastHelper.RegisterAumid();

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "run";
var apply = args.Contains("--apply") || args.Contains("-y");

switch (verb)
{
    case "run":
    case "service":
        await RunService(args);
        break;
    case "status":
        Cli.Status();
        break;
    case "restore":
        Cli.Restore(apply);
        break;
    case "undo":
        Cli.Undo(apply);
        break;
    case "baseline":
        Cli.Baseline(reset: args.Contains("--reset"));
        break;
    case "score":
        Cli.Score();
        break;
    case "serve":
        await ApiServer.Run();
        break;
    default:
        Cli.Help();
        break;
}

// Runs the background watcher (unchanged behavior): captures a baseline on first
// run, then every 5 min detects Windows Update and logs/toasts any regression.
static async Task RunService(string[] args)
{
    var host = Host.CreateDefaultBuilder(args)
        .ConfigureServices(services => services.AddHostedService<Worker>())
        .Build();

    await host.RunAsync();
}

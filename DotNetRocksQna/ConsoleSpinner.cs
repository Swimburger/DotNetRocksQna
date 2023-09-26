using Spectre.Console;

namespace DotNetRocksQna;

public static class ConsoleSpinner
{
    private static TaskCompletionSource? _completionSource;
    
    public static void Start(string status)
    {
        _completionSource = new TaskCompletionSource();
        AnsiConsole.Status()
            .StartAsync(status, _ => _completionSource.Task);
    }

    public static void Stop()
    {
        _completionSource?.SetResult();
    }
}
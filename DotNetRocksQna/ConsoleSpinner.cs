using Spectre.Console;

namespace DotNetRocksQna;

public static class ConsoleSpinner
{
    private static TaskCompletionSource completionSource;
    
    public static void Start(string status)
    {
        completionSource = new TaskCompletionSource();
        AnsiConsole.Status()
            .StartAsync(status, _ => completionSource.Task);
    }

    public static void Stop()
    {
        completionSource?.TrySetResult();
    }
}
namespace ScheduleApp.Desktop.Tests;

/// <summary>Waits out work a ViewModel started without handing back a task to await -- a
/// refresh kicked off by a property change.</summary>
internal static class Until
{
    public static async Task TrueAsync(Func<bool> condition, string? what = null)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            if (waited > 5000)
                throw new TimeoutException($"Gave up waiting for {what ?? "the condition"}.");
            await Task.Delay(10);
        }
    }
}

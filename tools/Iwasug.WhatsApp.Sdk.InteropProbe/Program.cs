using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Transport;

namespace Iwasug.WhatsApp.Sdk.InteropProbe;

/// <summary>
/// Thin entry point for the interop drift probe (OPS-02): one fresh-registration
/// connect against the live server, one classified exit code, and exactly one
/// final <c>PROBE_RESULT exit=&lt;n&gt; detail=&lt;text&gt;</c> stdout line the
/// live-interop workflow log-greps (09-04).
/// </summary>
internal static class Program
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private static async Task<int> Main()
    {
        // One throwaway identity per run: the probe never uses stored credentials.
        AccountId accountId = AccountId.Create("probe-" + Guid.NewGuid().ToString("N")[..8]);
        var runner = new ProbeRunner(
            accountId,
            () => new WebSocketFrameTransport(new Uri("wss://web.whatsapp.com/ws/chat")),
            DefaultTimeout);

        ProbeOutcome outcome;
        try
        {
            outcome = await runner.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The runner classifies every expected outcome; this net keeps the
            // process contract (one PROBE_RESULT line, exit vocabulary 0/10/20)
            // even on a bug — a crash would leave the workflow without a verdict.
            outcome = ProbeClassifier.FromException(ex);
        }

        Console.WriteLine($"PROBE_RESULT exit={outcome.ProcessExitCode} detail={outcome.Detail}");
        return outcome.ProcessExitCode;
    }
}

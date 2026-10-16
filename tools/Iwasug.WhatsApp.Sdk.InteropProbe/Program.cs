using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Transport;

namespace Iwasug.WhatsApp.Sdk.InteropProbe;

/// <summary>
/// Thin entry point for the interop drift probe (OPS-02): one fresh-registration
/// connect against the live server, one classified exit code, and exactly one
/// final <c>PROBE_RESULT exit=&lt;n&gt; detail=&lt;text&gt;</c> stdout line the
/// live-interop workflow log-greps (09-04). An argument-contract failure writes
/// one stderr message and exits 2 — disjoint from the outcome vocabulary.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (ProbeOptionsException ex)
        {
            Console.Error.WriteLine($"interop-probe: {ex.Message}");
            return 2;
        }

        // One throwaway identity per run: the probe never uses stored credentials.
        AccountId accountId = AccountId.Create("probe-" + Guid.NewGuid().ToString("N")[..8]);
        var runner = new ProbeRunner(
            accountId,
            () => new WebSocketFrameTransport(new Uri("wss://web.whatsapp.com/ws/chat")),
            options.Timeout);

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

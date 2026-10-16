using Iwasug.WhatsApp.Sdk.Pairing;

namespace Iwasug.WhatsApp.Sdk.InteropProbe;

/// <summary>
/// The probe's exit-code vocabulary (RESEARCH Pattern 3): three classified
/// outcomes, disjoint from the arg-contract failure code (2) the entry point
/// uses. The live workflow log-greps these values.
/// </summary>
public enum ProbeExitCode
{
    /// <summary>The server accepted the client version (a pairing ref was issued).</summary>
    Accepted = 0,

    /// <summary>Protocol drift: the server rejected this client build (405 class).</summary>
    Drift = 10,

    /// <summary>Transport/DNS/handshake/timeout: infra, never a version verdict.</summary>
    Infra = 20,
}

/// <summary>
/// One probe decision: the classified exit code plus a short, material-free
/// detail line for the workflow log (T-9-06: failure classes only, never key
/// material or store contents).
/// </summary>
/// <param name="ExitCode">The classified outcome.</param>
/// <param name="Detail">Short diagnostic detail (never key material).</param>
public sealed record ProbeOutcome(ProbeExitCode ExitCode, string Detail)
{
    /// <summary>The process exit code the entry point returns.</summary>
    public int ProcessExitCode => (int)ExitCode;
}

/// <summary>
/// Total classification from the pairing surface to the probe outcome
/// (RESEARCH Pattern 3): a server-issued <see cref="PairingStatus.Code"/> is
/// acceptance (proven without a phone), <see cref="PairingStatus.ClientOutdated"/>
/// is the 405 drift signal, and everything else is infra.
/// </summary>
public static class ProbeClassifier
{
    /// <summary>
    /// Maps every <see cref="PairingStatus"/> member (and any future member) to
    /// a probe outcome — total by construction, no throwing branch.
    /// </summary>
    /// <param name="status">The pairing status observed.</param>
    /// <returns>The classified outcome.</returns>
    public static ProbeOutcome FromStatus(PairingStatus status) => status switch
    {
        PairingStatus.Code => new ProbeOutcome(ProbeExitCode.Accepted, "server issued pairing ref"),
        PairingStatus.ClientOutdated => new ProbeOutcome(ProbeExitCode.Drift, "405 version rejection"),
        PairingStatus.Success => new ProbeOutcome(ProbeExitCode.Infra, "not a version verdict"),
        PairingStatus.Timeout => new ProbeOutcome(ProbeExitCode.Infra, "not a version verdict"),
        PairingStatus.LoggedOut => new ProbeOutcome(ProbeExitCode.Infra, "not a version verdict"),
        _ => new ProbeOutcome(ProbeExitCode.Infra, "not a version verdict"),
    };

    /// <summary>
    /// Maps a connect-side failure (transport/DNS/handshake) to infra. The
    /// detail carries the SDK exception contract's material-free message — the
    /// SDK never embeds key material in exception text (T-9-06).
    /// </summary>
    /// <param name="exception">The failure to classify.</param>
    /// <returns>The classified outcome.</returns>
    public static ProbeOutcome FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string message = exception.Message.ReplaceLineEndings(" ").Trim();
        if (message.Length > 200)
        {
            message = message[..200];
        }

        return new ProbeOutcome(ProbeExitCode.Infra, $"connect failure: {exception.GetType().Name}: {message}");
    }
}

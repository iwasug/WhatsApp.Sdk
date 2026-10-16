#pragma warning disable CS1591
namespace Iwasug.WhatsApp.Sdk.Messaging;

/// <summary>
/// Presence state surfaced by the Phase 8 presence API (EXT-02). The SDK makes no
/// implicit availability claim; the host drives every transition through
/// <see cref="Messenger.SendPresenceAsync"/>.
/// </summary>
public enum PresenceState
{
    /// <summary>Account is available.</summary>
    Available = 0,

    /// <summary>Account is unavailable.</summary>
    Unavailable = 1,

    /// <summary>Typing in a chat.</summary>
    Composing = 2,

    /// <summary>Recording audio in a chat.</summary>
    Recording = 3,

    /// <summary>Typing paused.</summary>
    Paused = 4,
}
namespace Iwasug.WhatsApp.Sdk.Domain;

/// <summary>
/// Typed domain record for the per-account ban-safety state stored by
/// <see cref="Stores.ISafetyStateStore"/> (D-04: typed domain objects, not byte blobs).
/// </summary>
/// <remarks>
/// Safety state is a single mutable row per account: <c>PutAsync</c> is last-write-wins,
/// and the store persists whatever the SDK's safety layer writes. Durability class
/// (D-05): must-not-lose — safety state is one of the four durable stores.
/// </remarks>
/// <param name="CreatedAt">When this account's warm-up timeline began (UTC).</param>
/// <param name="WarmupStage">Current stage of the account warm-up progression.</param>
public sealed record SafetyStateRecord(DateTimeOffset CreatedAt, int WarmupStage);

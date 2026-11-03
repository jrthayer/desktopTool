using System.Text.Json.Serialization;
namespace DesktopTool.Features.FenceLocker.Criteria;

/// <summary>Opens once you say why you need access this time. The reason is recorded in the unlock
/// history. There's nothing to configure.</summary>
public sealed class SpecialEventCriterion : LockCriterion
{
    [JsonIgnore]
    public override string UnlockType => "Special event";

    public override string Describe() => "Special event (say why)";

    /// <summary>A reason counts once it has real text in it.</summary>
    public static bool IsValidReason(string? reason) => !string.IsNullOrWhiteSpace(reason);
}

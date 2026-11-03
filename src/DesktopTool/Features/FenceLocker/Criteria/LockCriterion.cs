using System.Text.Json.Serialization;

namespace DesktopTool.Features.FenceLocker.Criteria;

/// <summary>One way into a locked fence. A lock can have several, and any one of them is enough
/// (see FenceLockService.IsLocked). Two kinds of criterion exist:
/// - passive ones that hold on their own (a time window - IsSatisfied), and
/// - ones that need something from the user in the unlock prompt (a password, a logged reason -
///   their prompt controls live in UI/CriterionUi).
///
/// Adding a criterion: a new subclass here, a [JsonDerivedType] line below so it saves and loads,
/// and one entry in UI/CriterionUi's registry for its editor and prompt.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TimeWindowCriterion), "time")]
[JsonDerivedType(typeof(SpecialEventCriterion), "event")]
[JsonDerivedType(typeof(PasswordCriterion), "password")]
public abstract class LockCriterion
{
    /// <summary>How an unlock through this criterion is labelled in the unlock history.</summary>
    [JsonIgnore]
    public abstract string UnlockType { get; }

    /// <summary>A short human description, e.g. "Weekdays 18:00–22:00".</summary>
    public abstract string Describe();

    /// <summary>True when this criterion lets the fence open right now with no input at all.</summary>
    public virtual bool IsSatisfied(DateTime now) => false;
}

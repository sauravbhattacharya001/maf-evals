namespace EvalFramework.Rules;

/// <summary>
/// Marks an exception as a deliberate rule block rather than a system failure.
/// </summary>
/// <remarks>
/// The distinction is the difference between a measurement and a lie. A blocked response is a real
/// outcome worth counting; a rate limit or an expired key is missing data. Recording the second as
/// the first produces a confident reliability number describing an API outage. An interface, rather
/// than a shared base class, keeps the framework free of any dependency on a particular agent.
/// </remarks>
public interface IRuleBlockedException
{
    RuleReport Report { get; }
}

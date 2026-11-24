using System.Reflection;

namespace AgentConnect.Tests.Unit;

/// <summary>Every T-id in section 11 has at least one test.</summary>
public sealed class TraceabilityTests
{
    [Fact]
    public void EverySpecTestId_HasATest()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in typeof(TraceabilityTests).Assembly.GetTypes().SelectMany(type => type.GetMethods()))
        {
            foreach (var attribute in method.GetCustomAttributesData())
            {
                if (attribute.AttributeType != typeof(TraitAttribute) || attribute.ConstructorArguments.Count != 2)
                {
                    continue;
                }

                if (attribute.ConstructorArguments[0].Value as string == "T" && attribute.ConstructorArguments[1].Value is string value)
                {
                    found.Add(value);
                }
            }
        }

        var missing = new List<string>();
        for (var number = 1; number <= 64; number++)
        {
            var id = "T-" + number.ToString();
            if (!found.Contains(id))
            {
                missing.Add(id);
            }
        }

        Assert.True(missing.Count == 0, "Missing " + string.Join(", ", missing));
    }
}

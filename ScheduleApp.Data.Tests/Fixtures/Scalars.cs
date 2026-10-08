using System.Reflection;
using Xunit;

namespace ScheduleApp.Data.Tests.Fixtures;

/// <summary>
/// Compares two entities column by column: every public read/write property of a value type or
/// string, which is every mapped column and none of the navigations (comparing those would walk
/// the parent/child cycles a read sets up). Asserts on each property by name, so a failure says
/// which column didn't round-trip.
/// </summary>
public static class Scalars
{
    public static void AssertEqual<T>(T expected, T actual)
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;
            if (!property.PropertyType.IsValueType && property.PropertyType != typeof(string)) continue;

            var expectedValue = property.GetValue(expected);
            var actualValue = property.GetValue(actual);
            Assert.True(Equals(expectedValue, actualValue),
                $"{typeof(T).Name}.{property.Name}: expected {expectedValue ?? "null"}, read back {actualValue ?? "null"}");
        }
    }
}

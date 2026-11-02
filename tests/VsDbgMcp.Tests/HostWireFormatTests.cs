using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;
using VsDbgMcp.Host;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// The extension writes replies and events with System.Text.Json; the shim reads them
    /// with Newtonsoft, and writes requests the other way. Every contract type, filled in
    /// throughout, has to come out the other side carrying the same JSON.
    /// </summary>
    public class HostWireFormatTests
    {
        static readonly JsonSerializerSettings Shim = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        static readonly System.Text.Json.JsonSerializerOptions Host = PipeServer.Formatter().JsonSerializerOptions;

        public static IEnumerable<object[]> ContractTypes() =>
            typeof(StopEvent).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic && t.Namespace == typeof(StopEvent).Namespace &&
                            t.GetConstructor(Type.EmptyTypes) != null && !typeof(Delegate).IsAssignableFrom(t))
                .OrderBy(t => t.Name)
                .Select(t => new object[] { t.Name });

        [Theory]
        [MemberData(nameof(ContractTypes))]
        public void Replies_written_by_the_extension_read_the_same_in_the_shim(string name)
        {
            var type = ContractType(name);
            var value = Filled(type, 0);
            var expected = JsonConvert.SerializeObject(value, Shim);

            var wire = System.Text.Json.JsonSerializer.Serialize(value, type, Host);
            var read = JsonConvert.DeserializeObject(wire, type, Shim);

            Assert.Equal(expected, JsonConvert.SerializeObject(read, Shim));
        }

        [Theory]
        [MemberData(nameof(ContractTypes))]
        public void Requests_written_by_the_shim_read_the_same_in_the_extension(string name)
        {
            var type = ContractType(name);
            var value = Filled(type, 0);
            var expected = JsonConvert.SerializeObject(value, Shim);

            var read = System.Text.Json.JsonSerializer.Deserialize(expected, type, Host);

            Assert.Equal(expected, JsonConvert.SerializeObject(read, Shim));
        }

        [Fact]
        public void The_samples_reach_nested_values()
        {
            var wire = System.Text.Json.JsonSerializer.Serialize(Filled(typeof(ThreadSummary), 0), Host);

            Assert.Contains("\"TopFrames\":[{", wire);
            Assert.Contains("\"File\":\"text ", wire);
            Assert.DoesNotContain("\"topFrames\"", wire);
        }

        [Fact]
        public void A_value_that_is_not_a_number_still_crosses()
        {
            var wire = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, double> { ["x"] = double.NaN }, Host);
            Assert.True(double.IsNaN(JsonConvert.DeserializeObject<Dictionary<string, double>>(wire, Shim)["x"]));
        }

        static Type ContractType(string name) => typeof(StopEvent).Assembly.GetTypes()
            .Single(t => t.Name == name && t.Namespace == typeof(StopEvent).Namespace);

        /// <summary>Every settable property given a value that is not its default, two levels deep.</summary>
        static object Filled(Type type, int depth)
        {
            var value = Activator.CreateInstance(type);
            var seed = 1;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0) continue;
                var sample = Sample(property.PropertyType, depth, seed++);
                if (sample == null) continue;

                if (property.CanWrite && property.SetMethod.IsPublic) property.SetValue(value, sample);
                else if (property.GetValue(value) is IList list && sample is IList items)
                    foreach (var item in items) list.Add(item);
            }
            return value;
        }

        static object Sample(Type type, int depth, int seed)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(string)) return "text " + seed + " \"quoted\" \\ é";
            if (underlying == typeof(int)) return seed;
            if (underlying == typeof(long)) return 10_000_000_000L + seed;
            if (underlying == typeof(uint)) return (uint)seed;
            if (underlying == typeof(ulong)) return (ulong)seed;
            if (underlying == typeof(double)) return seed + 0.25;
            if (underlying == typeof(float)) return seed + 0.5f;
            if (underlying == typeof(bool)) return true;
            if (underlying == typeof(DateTime)) return new DateTime(2026, 11, 2, 18, 55, 12, 123, DateTimeKind.Utc);
            if (underlying == typeof(Guid)) return new Guid("0123456789abcdef0123456789abcdef");
            if (underlying.IsEnum) return Enum.GetValues(underlying).Cast<object>().Last();
            if (underlying.IsArray)
            {
                var element = underlying.GetElementType();
                var one = Sample(element, depth, seed);
                if (one == null) return null;
                var array = Array.CreateInstance(element, 2);
                array.SetValue(one, 0);
                array.SetValue(Sample(element, depth, seed + 1), 1);
                return array;
            }
            if (underlying.IsGenericType)
            {
                var definition = underlying.GetGenericTypeDefinition();
                var arguments = underlying.GetGenericArguments();
                if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IEnumerable<>) ||
                    definition == typeof(IReadOnlyList<>))
                {
                    var one = Sample(arguments[0], depth, seed);
                    if (one == null) return null;
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(arguments[0]));
                    list.Add(one);
                    list.Add(Sample(arguments[0], depth, seed + 1));
                    return list;
                }
                if (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) ||
                    definition == typeof(IReadOnlyDictionary<,>))
                {
                    var key = Sample(arguments[0], depth, seed);
                    var item = Sample(arguments[1], depth, seed);
                    if (key == null || item == null) return null;
                    var map = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments));
                    map.Add(key, item);
                    return map;
                }
                return null;
            }
            if (underlying.IsClass && underlying.Namespace == typeof(StopEvent).Namespace &&
                underlying.GetConstructor(Type.EmptyTypes) != null)
                return depth >= 2 ? null : Filled(underlying, depth + 1);
            return null;
        }
    }
}

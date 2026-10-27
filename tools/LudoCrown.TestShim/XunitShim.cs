// Minimal xUnit-compatible API surface used by the Ludo Crown tests. DEVELOPMENT TOOL ONLY.
using System.Collections;
using System.Reflection;

namespace Xunit
{
    [AttributeUsage(AttributeTargets.Method)]
    public class FactAttribute : Attribute
    {
        public string? Skip { get; set; }
        public string? DisplayName { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TheoryAttribute : FactAttribute;

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class InlineDataAttribute(params object?[] data) : Attribute
    {
        public object?[] Data { get; } = data;
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class MemberDataAttribute(string memberName) : Attribute
    {
        public string MemberName { get; } = memberName;
    }

    public interface IAsyncLifetime
    {
        Task InitializeAsync();
        Task DisposeAsync();
    }

    public sealed class AssertException(string message) : Exception(message);

    public static class Assert
    {
        public static void True(bool condition, string? message = null) { if (!condition) Fail(message ?? "Expected true"); }
        public static void False(bool condition, string? message = null) { if (condition) Fail(message ?? "Expected false"); }
        public static void Null(object? value) { if (value is not null) Fail($"Expected null but was {value}"); }
        public static T NotNull<T>(T? value) where T : class { if (value is null) Fail("Expected not null"); return value!; }
        public static T NotNull<T>(T? value) where T : struct { if (value is null) Fail("Expected not null"); return value!.Value; }
        public static void Same(object? expected, object? actual) { if (!ReferenceEquals(expected, actual)) Fail("Expected same instance"); }
        public static void NotEqual<T>(T expected, T actual) { if (EqualityComparer<T>.Default.Equals(expected, actual)) Fail($"Expected not {expected}"); }

        public static void Equal(string? expected, string? actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal)) Fail($"Expected \"{expected}\" but was \"{actual}\"");
        }

        public static void Equal<T>(T expected, T actual)
        {
            if (expected is IEnumerable ee && actual is IEnumerable ae && expected is not string)
            {
                // Like xUnit: collections compare by contents.
                if (!ee.Cast<object?>().SequenceEqual(ae.Cast<object?>())) Fail($"Expected [{Show(expected)}] but was [{Show(actual)}]");
                return;
            }
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) Fail($"Expected {Show(expected)} but was {Show(actual)}");
        }

        public static void Equal<T>(IEnumerable<T>? expected, IEnumerable<T>? actual)
        {
            var e = expected?.ToList();
            var a = actual?.ToList();
            if (e is null && a is null) return;
            if (e is null || a is null || !e.SequenceEqual(a)) Fail($"Expected [{Show(e)}] but was [{Show(a)}]");
        }

        public static void InRange<T>(T actual, T low, T high) where T : IComparable<T>
        {
            if (actual.CompareTo(low) < 0 || actual.CompareTo(high) > 0) Fail($"{actual} not in [{low}, {high}]");
        }

        public static void Contains<T>(IEnumerable<T> collection, Predicate<T> filter)
        {
            if (!collection.Any(x => filter(x))) Fail("No item matched the filter");
        }

        public static void Contains<T>(T expected, IEnumerable<T> collection)
        {
            if (!collection.Contains(expected)) Fail($"{expected} not found");
        }

        public static void Contains(string expected, string? actual)
        {
            if (actual is null || !actual.Contains(expected, StringComparison.Ordinal)) Fail($"\"{expected}\" not found in \"{actual}\"");
        }

        public static void StartsWith(string expected, string? actual)
        {
            if (actual is null || !actual.StartsWith(expected, StringComparison.Ordinal)) Fail($"\"{actual}\" does not start with \"{expected}\"");
        }

        public static void DoesNotContain<T>(IEnumerable<T> collection, Predicate<T> filter)
        {
            if (collection.Any(x => filter(x))) Fail("An item matched the filter");
        }

        public static void Empty(IEnumerable collection)
        {
            if (collection.Cast<object>().Any()) Fail("Expected empty collection");
        }

        public static void NotEmpty(IEnumerable collection)
        {
            if (!collection.Cast<object>().Any()) Fail("Expected non-empty collection");
        }

        public static T Single<T>(IEnumerable<T> collection)
        {
            var list = collection.ToList();
            if (list.Count != 1) Fail($"Expected one item but found {list.Count}");
            return list[0];
        }

        public static void All<T>(IEnumerable<T> collection, Action<T> action)
        {
            foreach (var item in collection) action(item);
        }

        public static T IsType<T>(object? value)
        {
            if (value is not T t || value.GetType() != typeof(T)) { Fail($"Expected {typeof(T).Name} but was {value?.GetType().Name ?? "null"}"); return default!; }
            return t;
        }

        public static T Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T ex) when (ex.GetType() == typeof(T)) { return ex; }
            catch (Exception ex) { Fail($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}"); }
            Fail($"Expected {typeof(T).Name} but nothing was thrown");
            return null!;
        }

        public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T ex) when (ex.GetType() == typeof(T)) { return ex; }
            catch (Exception ex) { Fail($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}"); }
            Fail($"Expected {typeof(T).Name} but nothing was thrown");
            return null!;
        }

        private static string Show(object? o) => o switch
        {
            null => "null",
            string s => $"\"{s}\"",
            IEnumerable e => string.Join(", ", e.Cast<object?>().Select(Show)),
            _ => o.ToString() ?? "",
        };

        private static void Fail(string message) => throw new AssertException(message);
    }
}

namespace LudoCrown.TestShim
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var filter = args.FirstOrDefault();
            var passed = 0;
            var failed = new List<string>();
            var types = typeof(Program).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic)
                .OrderBy(t => t.FullName);

            foreach (var type in types)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).OrderBy(m => m.Name))
                {
                    var fact = method.GetCustomAttribute<Xunit.FactAttribute>();
                    if (fact is null) continue;
                    var name = $"{type.Name}.{method.Name}";
                    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (fact.Skip is not null) { Console.WriteLine($"  SKIP {name}: {fact.Skip}"); continue; }

                    foreach (var data in DataRows(type, method))
                    {
                        var label = data.Length == 0 ? name : $"{name}({string.Join(", ", data.Select(d => d?.ToString() ?? "null"))})";
                        try
                        {
                            var instance = Activator.CreateInstance(type)!;
                            if (instance is Xunit.IAsyncLifetime life) await life.InitializeAsync();
                            try
                            {
                                var result = method.Invoke(instance, Convert(method, data));
                                if (result is Task task) await task;
                            }
                            finally
                            {
                                if (instance is Xunit.IAsyncLifetime life2) await life2.DisposeAsync();
                                if (instance is IAsyncDisposable ad) await ad.DisposeAsync();
                                else if (instance is IDisposable d) d.Dispose();
                            }
                            passed++;
                        }
                        catch (Exception ex)
                        {
                            var inner = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex;
                            failed.Add($"{label}\n      {inner.GetType().Name}: {inner.Message}");
                        }
                    }
                }
            }

            foreach (var f in failed) Console.WriteLine($"  FAIL {f}");
            Console.WriteLine($"\nTotal: {passed + failed.Count}, Passed: {passed}, Failed: {failed.Count}");
            return failed.Count == 0 ? 0 : 1;
        }

        private static IEnumerable<object?[]> DataRows(Type type, MethodInfo method)
        {
            var inline = method.GetCustomAttributes<Xunit.InlineDataAttribute>().ToList();
            var members = method.GetCustomAttributes<Xunit.MemberDataAttribute>().ToList();
            if (inline.Count == 0 && members.Count == 0) { yield return Array.Empty<object?>(); yield break; }
            foreach (var i in inline) yield return i.Data;
            foreach (var m in members)
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var source = type.GetMethod(m.MemberName, flags)?.Invoke(null, null)
                             ?? type.GetProperty(m.MemberName, flags)?.GetValue(null);
                foreach (var row in (IEnumerable<object?[]>)source!) yield return row;
            }
        }

        private static object?[] Convert(MethodInfo method, object?[] data)
        {
            var ps = method.GetParameters();
            var result = new object?[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                var v = i < data.Length ? data[i] : null;
                var target = Nullable.GetUnderlyingType(ps[i].ParameterType) ?? ps[i].ParameterType;
                result[i] = v is null || target.IsInstanceOfType(v) ? v
                    : target.IsEnum ? Enum.ToObject(target, v)
                    : System.Convert.ChangeType(v, target);
            }
            return result;
        }
    }
}

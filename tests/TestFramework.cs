// TestFramework — a small self-hosted unit-test harness.
//
// This machine has no .NET SDK and no NuGet cache, so MSTest/xUnit/NUnit
// cannot be restored or run here. Rather than ship tests nobody can execute,
// this is a real (if deliberately small) test framework with the same shape:
// attributes mark test classes and methods, assertions throw on failure, and
// the runner reports pass/fail per case and sets a non-zero exit code.
//
// It is intentionally minimal — discovery is by reflection over the assembly,
// which is all a suite this size needs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Dsh.Msys2Installer.Tests
{
    /// <summary>Marks a class as a test fixture.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TestClassAttribute : Attribute { }

    /// <summary>Marks a parameterless method as a test case.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestMethodAttribute : Attribute { }

    /// <summary>
    /// Marks a test that cannot run in the current environment. The runner
    /// reports it as skipped with the given reason rather than passing it
    /// silently — an unrunnable check must never look like a green one.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class SkipAttribute : Attribute
    {
        public readonly string Reason;
        public SkipAttribute(string reason) { Reason = reason; }
    }

    /// <summary>Thrown when an assertion fails.</summary>
    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown by a test body that cannot run in this environment, e.g. because
    /// MSYS2 cannot start in a confined process. The runner reports it as
    /// skipped with the reason: an unrunnable check must never look green, and
    /// must not look like a failure of the code either.
    /// </summary>
    public sealed class SkipException : Exception
    {
        public SkipException(string message) : base(message) { }
    }

    /// <summary>Assertions. Each failure throws with a message naming the expectation.</summary>
    public static class Assert
    {
        public static void IsTrue(bool condition, string message)
        {
            if (!condition) throw new AssertionException("expected true: " + message);
        }

        public static void IsFalse(bool condition, string message)
        {
            if (condition) throw new AssertionException("expected false: " + message);
        }

        public static void AreEqual(object expected, object actual, string message)
        {
            bool equal = expected == null ? actual == null : expected.Equals(actual);
            if (!equal)
            {
                throw new AssertionException(
                    message + "\n    expected: " + Describe(expected) + "\n    actual:   " + Describe(actual));
            }
        }

        public static void AreNotEqual(object unexpected, object actual, string message)
        {
            bool equal = unexpected == null ? actual == null : unexpected.Equals(actual);
            if (equal)
                throw new AssertionException(message + "\n    both were: " + Describe(actual));
        }

        public static void IsNull(object value, string message)
        {
            if (value != null)
                throw new AssertionException(message + "\n    expected null, got: " + Describe(value));
        }

        public static void IsNotNull(object value, string message)
        {
            if (value == null) throw new AssertionException("expected non-null: " + message);
        }

        /// <summary>Assert that `haystack` contains `needle`.</summary>
        public static void Contains(string needle, string haystack, string message)
        {
            if (haystack == null)
                throw new AssertionException(message + "\n    haystack was null, expected to contain: " + Describe(needle));
            if (haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
            {
                throw new AssertionException(
                    message + "\n    expected to contain: " + Describe(needle)
                    + "\n    actual text:         " + Describe(haystack));
            }
        }

        /// <summary>Assert that `haystack` does NOT contain `needle`.</summary>
        public static void DoesNotContain(string needle, string haystack, string message)
        {
            if (haystack != null && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0)
            {
                throw new AssertionException(
                    message + "\n    expected NOT to contain: " + Describe(needle)
                    + "\n    actual text:             " + Describe(haystack));
            }
        }

        /// <summary>Assert that a file exists, reporting the path when it does not.</summary>
        public static void FileExists(string path, string message)
        {
            if (!System.IO.File.Exists(path))
                throw new AssertionException(message + "\n    missing file: " + path);
        }

        /// <summary>Run an action and assert it throws.</summary>
        public static void Throws<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception other)
            {
                throw new AssertionException(
                    message + "\n    expected " + typeof(TException).Name
                    + ", got " + other.GetType().Name + ": " + other.Message);
            }
            throw new AssertionException(message + "\n    expected " + typeof(TException).Name + ", nothing was thrown");
        }

        private static string Describe(object value)
        {
            if (value == null) return "(null)";
            string text = value.ToString();
            if (text.Length > 600) text = text.Substring(0, 600) + "…(truncated)";
            return "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }
    }

    /// <summary>
    /// Where tests may create scratch files.
    ///
    /// Deliberately NOT the system temp directory. csc.exe writes intermediate
    /// files beside its /out target, so compiling the shim into %TEMP% fails
    /// under a workspace-confined process with CS1567 ("Error generating Win32
    /// resource"). A scratch directory under the current directory keeps the
    /// suite runnable in a confined session, which is the common case.
    /// </summary>
    public static class TestScratch
    {
        public static string Root()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), "test-scratch");
            Directory.CreateDirectory(root);
            return root;
        }

        /// <summary>A fresh, uniquely-named directory under the scratch root.</summary>
        public static string NewDirectory(string label)
        {
            string dir = Path.Combine(
                Root(), label + "-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Delete a scratch directory, ignoring failure.</summary>
        public static void Cleanup(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception) { }
        }
    }

    /// <summary>One test's outcome.</summary>
    public sealed class TestOutcome
    {
        public string Name;
        public bool Passed;
        public bool Skipped;
        public string Message;
        public long Milliseconds;
    }

    /// <summary>Discovers and runs every [TestClass]/[TestMethod] in an assembly.</summary>
    public static class TestRunner
    {
        public static List<TestOutcome> RunAll(Assembly assembly, Action<string> log)
        {
            var outcomes = new List<TestOutcome>();

            foreach (Type type in assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(TestClassAttribute), false).Length == 0) continue;

                var methods = new List<MethodInfo>();
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.GetCustomAttributes(typeof(TestMethodAttribute), false).Length > 0)
                        methods.Add(method);
                }
                methods.Sort(delegate(MethodInfo a, MethodInfo b)
                {
                    return string.CompareOrdinal(a.Name, b.Name);
                });

                foreach (MethodInfo method in methods)
                {
                    var outcome = new TestOutcome();
                    outcome.Name = type.Name + "." + method.Name;

                    var skips = method.GetCustomAttributes(typeof(SkipAttribute), false);
                    if (skips.Length > 0)
                    {
                        outcome.Skipped = true;
                        outcome.Message = ((SkipAttribute)skips[0]).Reason;
                        outcomes.Add(outcome);
                        if (log != null) log("SKIP " + outcome.Name + " — " + outcome.Message);
                        continue;
                    }

                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        object instance = method.IsStatic ? null : Activator.CreateInstance(type);
                        method.Invoke(instance, null);
                        outcome.Passed = true;
                        if (log != null) log("ok   " + outcome.Name);
                    }
                    catch (TargetInvocationException wrapper)
                    {
                        var inner = wrapper.InnerException ?? wrapper;
                        if (inner is SkipException)
                        {
                            outcome.Skipped = true;
                            outcome.Message = inner.Message;
                            if (log != null) log("SKIP " + outcome.Name + " — " + outcome.Message);
                        }
                        else
                        {
                            outcome.Passed = false;
                            outcome.Message = Describe(inner);
                            if (log != null) log("FAIL " + outcome.Name + "\n       " + Indent(outcome.Message));
                        }
                    }
                    catch (SkipException skip)
                    {
                        outcome.Skipped = true;
                        outcome.Message = skip.Message;
                        if (log != null) log("SKIP " + outcome.Name + " — " + outcome.Message);
                    }
                    catch (Exception error)
                    {
                        outcome.Passed = false;
                        outcome.Message = Describe(error);
                        if (log != null) log("FAIL " + outcome.Name + "\n       " + Indent(outcome.Message));
                    }
                    finally
                    {
                        watch.Stop();
                        outcome.Milliseconds = watch.ElapsedMilliseconds;
                    }
                    outcomes.Add(outcome);
                }
            }
            return outcomes;
        }

        private static string Describe(Exception error)
        {
            var sb = new StringBuilder();
            sb.Append(error.GetType().Name).Append(": ").Append(error.Message);
            return sb.ToString();
        }

        private static string Indent(string text)
        {
            return text.Replace("\n", "\n       ");
        }
    }
}

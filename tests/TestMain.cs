// TestMain — runs the whole suite and reports results.
//
// Usage:
//   msys2-tests.exe             run every test
//   msys2-tests.exe --filter X  run only tests whose full name contains X
//   msys2-tests.exe --list      list discovered tests without running them
//
// Exit codes: 0 all passed (skips allowed), 1 at least one failure.

using System;
using System.Collections.Generic;
using System.Reflection;

namespace Dsh.Msys2Installer.Tests
{
    internal static class TestMain
    {
        private static int Main(string[] args)
        {
            string filter = null;
            bool listOnly = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
                else if (args[i] == "--list") listOnly = true;
                else if (args[i] == "--help" || args[i] == "-h")
                {
                    Console.WriteLine("usage: msys2-tests.exe [--filter substring] [--list]");
                    return 0;
                }
            }

            Console.WriteLine("DSH MSYS2 installer — test suite");
            Console.WriteLine("================================");
            Console.WriteLine();

            Assembly assembly = Assembly.GetExecutingAssembly();
            List<TestOutcome> outcomes = TestRunner.RunAll(assembly, listOnly ? null : new Action<string>(Console.WriteLine));

            // Apply the filter after discovery so --list can show everything.
            int passed = 0, failed = 0, skipped = 0;
            var failures = new List<TestOutcome>();

            foreach (TestOutcome outcome in outcomes)
            {
                if (filter != null && outcome.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (listOnly)
                {
                    Console.WriteLine(outcome.Name);
                    continue;
                }

                if (outcome.Skipped) skipped++;
                else if (outcome.Passed) passed++;
                else { failed++; failures.Add(outcome); }
            }

            if (listOnly) return 0;

            Console.WriteLine();
            Console.WriteLine("--------------------------------");
            Console.WriteLine("passed:  " + passed);
            Console.WriteLine("failed:  " + failed);
            Console.WriteLine("skipped: " + skipped);
            Console.WriteLine("total:   " + (passed + failed + skipped));

            if (failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("FAILURES");
                foreach (TestOutcome failure in failures)
                {
                    Console.WriteLine();
                    Console.WriteLine("  " + failure.Name);
                    foreach (string line in failure.Message.Split('\n'))
                        Console.WriteLine("      " + line);
                }
            }

            if (skipped > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Skipped checks (not verified in this run):");
                foreach (TestOutcome outcome in outcomes)
                {
                    if (filter != null && outcome.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!outcome.Skipped) continue;
                    Console.WriteLine("  " + outcome.Name + " — " + outcome.Message);
                }
            }

            Console.WriteLine();
            if (failed > 0)
            {
                Console.WriteLine("RESULT: FAILED");
                return 1;
            }
            Console.WriteLine("RESULT: PASSED" + (skipped > 0 ? " (with " + skipped + " skipped)" : ""));
            return 0;
        }
    }
}

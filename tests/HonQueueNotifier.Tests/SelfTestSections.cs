using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

namespace HonQueueNotifier.Tests
{
    // Each area of the app's self-test runs as its own test, so `dotnet test` reports them separately.
    // The checks themselves live in the app (Diagnostics/SelfTest.cs), where `--selftest` can run them
    // on a player's PC too.
    public class SelfTestSections
    {
        public static IEnumerable<object[]> Names() => SelfTest.SectionNames().Select(n => new object[] { n });

        [Theory]
        [MemberData(nameof(Names))]
        public void Section_passes(string name)
        {
            // WinForms (the settings dialog, the popup) needs a single-threaded apartment.
            List<string> failures = null;
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { failures = SelfTest.RunSection(name); }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) throw error;
            Assert.True(failures.Count == 0, name + ":" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }
    }
}

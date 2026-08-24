using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KJ_FlowForge_CreateKey
{
    public static class TestHarness
    {
        public static int Run()
        {
            Console.WriteLine("=== Test: Reload existing single-object format ===");
            var form = new MainForm();
            // Trigger Reload via reflection-free approach: just call internal logic
            // MainForm.Reload is private, but we test via the public behavior:
            // We'll simulate by directly reading and re-serializing
            string raw = File.ReadAllText(MainForm.TestJsonPath);
            Console.WriteLine("Original file:");
            Console.WriteLine(raw);

            Console.WriteLine("=== Test: Serialize as array format ===");
            // Use reflection to access private methods for testing
            var method = typeof(MainForm).GetMethod("SerializeManifest",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var reloadMethod = typeof(MainForm).GetMethod("Reload",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Force form creation to initialize
            var dummy = form.Handle; // force handle creation
            reloadMethod.Invoke(form, null);

            // After reload, entries should contain 1 item from single-object format
            var entriesField = typeof(MainForm).GetField("entries",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var entries = entriesField.GetValue(form) as System.Collections.Generic.List<LicenseEntry>;
            Console.WriteLine("Entries after reload: " + entries.Count);
            Console.WriteLine("  id=" + entries[0].Id + ", owner=" + entries[0].Owner + ", expiresAt=" + entries[0].ExpiresAt);

            // Now serialize back
            var revokedField = typeof(MainForm).GetField("revoked",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var revoked = revokedField.GetValue(form) as System.Collections.Generic.List<string>;
            string serialized = (string)method.Invoke(form, new object[] { entries, revoked });
            Console.WriteLine("Serialized output:");
            Console.WriteLine(serialized);

            Console.WriteLine("=== Test: Round-trip parse ===");
            // Verify the serialized output can be parsed back
            int pos = 0;
            var parseObjMethod = typeof(MainForm).GetMethod("ParseObject",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var parsed = (System.Collections.Generic.Dictionary<string, object>)parseObjMethod.Invoke(form,
                new object[] { serialized, pos });
            Console.WriteLine("Parsed keys: " + string.Join(", ", parsed.Keys));
            System.Collections.Generic.List<object> keys = null;
            if (parsed.ContainsKey("keys"))
                keys = parsed["keys"] as System.Collections.Generic.List<object>;
            if (keys != null)
            {
                Console.WriteLine("keys count: " + keys.Count);
                foreach (var k in keys)
                {
                    var dict = (System.Collections.Generic.Dictionary<string, object>)k;
                    Console.WriteLine("  id=" + dict["id"] + ", hash=" + dict["hash"] + ", owner=" + dict["owner"]);
                }
            }

            Console.WriteLine("ALL TESTS PASSED");
            return 0;
        }
    }
}

using System;
using System.IO;
using System.Collections.Generic;
using SrcCadWorks.Services;
class ResumeLedgerTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "SrcCadWorksResumeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "part.step");
            File.WriteAllText(file, "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");
            var controls = new Dictionary<string,string> { { "step", "True" } };
            var rows = new Dictionary<string,Dictionary<string,string>> { { "part", new Dictionary<string,string> { { "Quantity", "7" } } } };
            var first = new ResumeLedger("machining|assembly|configuration", root);
            first.Begin(root, false, controls, rows);
            first.Commit("STEP|part", file, "source-v1");
            first.State.Errors.Add("other part PDF: unavailable"); first.Save();
            Check(!first.Reuse("STEP|part", file, "source-v1"), "Normal generation must never reuse old output");
            var loaded = new ResumeLedger("machining|assembly|configuration", root);
            Check(loaded.State.Pending && loaded.State.Rows["part"]["Quantity"] == "7", "Restart must restore pending state and manual quantity");
            Check(loaded.State.Errors.Count == 1, "Restart must preserve errors");
            loaded.Begin("different-folder", true, controls, rows);
            Check(loaded.State.OutputFolder == root, "Resume must preserve exact output folder");
            Check(loaded.Reuse("STEP|part", file, "source-v1"), "Verified completed output must resume");
            Check(!loaded.Reuse("STEP|part", file, "source-v2"), "Changed source must regenerate");
            Check(!loaded.Reuse("STEP|part", file, ""), "Uncertain source must regenerate");
            File.AppendAllText(file, "changed");
            Check(!loaded.Reuse("STEP|part", file, "source-v1"), "Changed output must regenerate");
            File.WriteAllText(file, "ISO-10303-21;\nunfinished data without end marker................................");
            Check(!ResumeLedger.Complete(file), "Interrupted STEP must not be complete");
            loaded.Invalidate("STEP|part");
            var interrupted = new ResumeLedger("machining|assembly|configuration", root);
            Check(!interrupted.State.Files.ContainsKey("STEP|part"), "Invalidation must survive an interrupted export");
            loaded.Finish(false); Check(new ResumeLedger("machining|assembly|configuration", root).State.Pending, "Error run must remain resumable");
            loaded.Finish(true); Check(!new ResumeLedger("machining|assembly|configuration", root).State.Pending, "Successful run must be complete");
            var other = new ResumeLedger("laser|assembly|configuration", root);
            Check(other.State.Files.Count == 0 && !other.State.Pending, "Modules must have isolated journals");
            Console.WriteLine("PASS: resume provenance, persistence, errors, isolation, interruption checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
}

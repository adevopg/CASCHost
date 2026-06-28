using System;
using System.Linq;
using System.Text;
using CASCEdit;
using CASCEdit.IO;
using CASCEdit.Helpers;
using CASCEdit.Handlers;
using CASCEdit.Structs;
using System.IO;

namespace CASCExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
            string BASEPATH = (args.Length > 0 && Directory.Exists(args[0])) ? args[0] : AppContext.BaseDirectory;

            if (!File.Exists(Path.Combine(BASEPATH, ".build.info")))
            {
                Console.WriteLine("Error: Missing .build.info.");
                System.Threading.Thread.Sleep(1500);
                Environment.Exit(0);
            }

            // Diagnostic: parse the live root with the real handler to confirm format compatibility.
            if (args.Contains("verifyroot"))
            {
                VerifyRoot(BASEPATH);
                return;
            }

            // Diagnostic: inject a custom file and write+reread the root in an isolated temp dir.
            if (args.Contains("roundtriproot"))
            {
                RoundTripRoot(BASEPATH);
                return;
            }

            var settings = new CASSettings() { BasePath = BASEPATH, Basic = true };
            CASContainer.Open(settings);

            if (!CASContainer.ExtractSystemFiles(Path.Combine(BASEPATH, "_SystemFiles")))
            {
                Console.WriteLine("Please ensure that you have a fully downloaded client.");
                System.Threading.Thread.Sleep(3000);
            }

            CASContainer.Close();
        }

        static void VerifyRoot(string basepath)
        {
            var settings = new CASSettings() { BasePath = basepath, Basic = true, Locale = LocaleFlags.enUS };
            CASContainer.Open(settings);
            CASContainer.OpenLocalIndices();
            CASContainer.OpenEncoding();
            CASContainer.OpenRoot(settings.Locale);

            var rh = CASContainer.RootHandler;
            if (rh == null || rh.GlobalRoot == null)
            {
                Console.WriteLine("ROOT VERIFY FAILED: handler/global root is null.");
                Environment.Exit(1);
            }

            int totalEntries = rh.Chunks.Sum(c => c.Entries.Count);
            uint maxId = (uint)rh.Chunks.SelectMany(c => c.Entries).Select(e => e.FileDataId).DefaultIfEmpty(0u).Max();

            Console.WriteLine("ROOT VERIFY OK");
            Console.WriteLine($"  Blocks........: {rh.Chunks.Count}");
            Console.WriteLine($"  Total entries.: {totalEntries}");
            Console.WriteLine($"  Max FileDataID: {maxId}");
            Console.WriteLine($"  GlobalRoot....: ContentFlags={rh.GlobalRoot.ContentFlags} LocaleFlags={rh.GlobalRoot.LocaleFlags} Entries={rh.GlobalRoot.Entries.Count}");

            // Correctness: every parsed entry's content key must exist in the encoding table.
            // Sample across the whole root - if the layout were misread these would miss.
            var all = rh.Chunks.SelectMany(c => c.Entries).ToList();
            int sampled = 0, inEnc = 0;
            for (int i = 0; i < all.Count; i += Math.Max(1, all.Count / 2000))
            {
                sampled++;
                if (CASContainer.EncodingHandler.CEKeys.ContainsKey(all[i].CEKey)) inEnc++;
            }
            Console.WriteLine($"  Encoding cross-check: {inEnc}/{sampled} sampled content keys present in encoding ({100.0 * inEnc / sampled:F1}%)");

            CASContainer.Close();
        }

        static void RoundTripRoot(string basepath)
        {
            // Redirect ALL writes to a throwaway dir so the real client is never touched.
            string outDir = Path.Combine(Path.GetTempPath(), "casc_rt_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(outDir);

            var settings = new CASSettings()
            {
                BasePath = basepath,
                Basic = true,
                Locale = LocaleFlags.enUS,
                OutputPath = outDir,
                SystemFilesPath = outDir,
            };

            CASContainer.Open(settings);
            CASContainer.OpenLocalIndices();
            CASContainer.OpenEncoding();
            CASContainer.OpenRoot(settings.Locale);

            // Snapshot the original id->ckey map so we can prove the rewrite preserves every
            // Blizzard entry (FixOffsets delta encoding must round-trip exactly).
            var before = CASContainer.RootHandler.Chunks
                .SelectMany(c => c.Entries)
                .GroupBy(e => e.FileDataId)
                .ToDictionary(g => g.Key, g => g.First().CEKey.ToString());

            // Build a custom file and push it through the real encode pipeline.
            string cascpath = "custom/caschost_343_test.txt";
            byte[] data = Encoding.UTF8.GetBytes("CASCHost WoW Classic 3.4.3 round-trip OK");

            var file = new CASFile(data, EncodingType.ZLib, 9);
            var blte = DataHandler.Write(WriteMode.CDN, file);
            blte.CEKey = file.DataHash; // content key = md5 of the raw (uncompressed) data
            blte.Path = cascpath;
            string expectedCEKey = blte.CEKey.ToString();

            CASContainer.EncodingHandler.AddEntry(blte);
            CASContainer.RootHandler.AddEntry(cascpath, blte);

            uint addedId = CASContainer.RootHandler.GetEntry(cascpath)?.FileDataId ?? 0;
            var rootRes = CASContainer.RootHandler.Write();
            Console.WriteLine($"Wrote new root: EKey={rootRes.EKey} CEKey={rootRes.CEKey}");
            Console.WriteLine($"Custom entry: path={cascpath} FileDataID={addedId} CEKey={expectedCEKey}");

            // Re-read the freshly written root with a clean handler instance.
            var reread = new RootHandler(DataHandler.ReadDirect(rootRes.OutPath), settings.Locale);
            var found = reread.GetEntry(cascpath);

            if (found == null)
            {
                Console.WriteLine("ROUND-TRIP FAILED: custom entry not found after reread.");
                Environment.Exit(1);
            }

            bool keyOk = found.CEKey.ToString() == expectedCEKey;
            bool idOk = found.FileDataId == addedId;
            Console.WriteLine($"Reread entry: FileDataID={found.FileDataId} CEKey={found.CEKey}");
            Console.WriteLine($"  CEKey match : {keyOk}");
            Console.WriteLine($"  FileID match: {idOk}");
            Console.WriteLine($"  Reread blocks={reread.Chunks.Count} GlobalRoot(ContentFlags={reread.GlobalRoot.ContentFlags},Locale={reread.GlobalRoot.LocaleFlags},Entries={reread.GlobalRoot.Entries.Count})");

            // Full-fidelity check: every original FileDataID must reread with the same content key.
            var after = reread.Chunks
                .SelectMany(c => c.Entries)
                .GroupBy(e => e.FileDataId)
                .ToDictionary(g => g.Key, g => g.First().CEKey.ToString());

            int preserved = 0, drifted = 0;
            foreach (var kv in before)
            {
                if (after.TryGetValue(kv.Key, out var ck) && ck == kv.Value) preserved++;
                else drifted++;
            }
            Console.WriteLine($"  Blizzard entries preserved: {preserved}/{before.Count} (drifted={drifted})");
            Console.WriteLine($"  Total reread entries: {after.Count} (expected {before.Count + 1})");

            bool fidelityOk = drifted == 0 && after.Count == before.Count + 1;
            Console.WriteLine(keyOk && idOk && fidelityOk ? "ROUND-TRIP OK" : "ROUND-TRIP MISMATCH");

            try { Directory.Delete(outDir, true); } catch { }
            CASContainer.Close();
        }
    }
}
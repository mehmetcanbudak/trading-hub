import json, os, shutil, subprocess, tempfile, unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DLL = ROOT / "tools/TradingHub.I18n/bin/Release/net10.0/TradingHub.I18n.dll"
DOTNET = shutil.which("dotnet") or os.path.expanduser("~/.dotnet/dotnet")

SOURCE = '''
class A
{
    string Label = "Usage:";
    string Time = "12:30";
    string Joined(bool ok) => "Result: " + (ok ? "accepted" : "rejected") + " by the venue";
    string Many(int n) => "Total " + n + " orders";
}
'''


@unittest.skipUnless(DLL.exists(), "build tools/TradingHub.I18n in Release first")
class ExtractorTests(unittest.TestCase):
    def extract(self):
        with tempfile.TemporaryDirectory() as work:
            src = Path(work, "src"); src.mkdir()
            (src / "A.cs").write_text(SOURCE)
            out = Path(work, "catalog.json")
            subprocess.run([DOTNET, str(DLL), "extract", str(src), str(out)], check=True, capture_output=True)
            return json.loads(out.read_text())

    def test_single_word_label_with_a_trailing_colon_is_text(self):
        keys = self.extract()
        self.assertIn("Usage:", keys)
        self.assertNotIn("12:30", keys)

    def test_a_conditional_inside_a_concatenation_yields_each_complete_sentence(self):
        keys = self.extract()
        self.assertIn("Result: accepted by the venue", keys)
        self.assertIn("Result: rejected by the venue", keys)
        self.assertNotIn("Result: {0} by the venue", keys)
        self.assertIn("Total {0} orders", keys)


if __name__ == "__main__":
    unittest.main()

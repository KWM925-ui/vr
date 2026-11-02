"""Clones may use another drive; saved CAD references must remain contained."""

from pathlib import Path
import tempfile
import unittest

from cad_sources import ROOT, native_source


class CadSourceTests(unittest.TestCase):
    def test_original_path_is_rebased_to_an_explicit_checkout(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "tmp", prefix="cad_path_") as folder:
            expected = Path(folder).resolve() / "10月11日修改" / "1.34 船桨.SLDPRT"
            self.assertEqual(native_source(r"F:\vr视觉\10月11日修改\1.34 船桨.SLDPRT", root=folder), expected)

    def test_unrelated_or_traversing_references_are_rejected(self):
        for value in (r"C:\elsewhere\part.SLDPRT", r"F:\vr视觉\..\part.SLDPRT",
                      r"F:\vr视觉\10月11日修改\..\secret", r"F:\vr视觉\tmp\part.SLDPRT"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                native_source(value)


if __name__ == "__main__":
    unittest.main()

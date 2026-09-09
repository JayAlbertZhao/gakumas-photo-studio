import json
from pathlib import Path
import re
import unittest
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]


class DocumentationLinksTests(unittest.TestCase):
    def test_published_markdown_links_resolve_to_published_files(self):
        public = set(json.loads((ROOT / 'public-files.json').read_text(encoding='utf-8'))['files'])
        checked = 0
        for name in sorted(public):
            if not name.endswith('.md'):
                continue
            source = ROOT / name
            for target in re.findall(r'\[[^\]]*\]\(([^\s)]+)\)', source.read_text(encoding='utf-8')):
                parsed = urlsplit(target)
                if parsed.scheme or parsed.netloc or not parsed.path:
                    continue
                with self.subTest(document=name, target=target):
                    path = (source.parent / unquote(parsed.path)).resolve()
                    self.assertTrue(path.is_relative_to(ROOT))
                    self.assertTrue(path.is_file())
                    self.assertIn(path.relative_to(ROOT).as_posix(), public)
                    checked += 1
        self.assertGreater(checked, 20)


if __name__ == '__main__':
    unittest.main()

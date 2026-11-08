"""CODEEXPORT_01 — stdlib regressions; retain this run, replace previous fixtures.
Spec: docs/05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-CODEEXPORT.
Run: python -B -m unittest discover -s tests/DeveloperTools.Tests -v
"""
import sys
sys.dont_write_bytecode = True
import contextlib
import hashlib
import importlib.util
import io
import math
import shutil
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('pull_code_exporter', REPO / 'developer_tools/Pull_Code_files.py')
exporter = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = exporter
spec.loader.exec_module(exporter)


def setUpModule():
    # TESTCLEAN_01: exactly this suite's generated tree, never source or user exports.
    base = REPO / 'artifacts' / 'code-export-tests'
    if base.resolve() != REPO.resolve() / 'artifacts' / 'code-export-tests':
        raise RuntimeError('Refusing redirected test output')
    for parent in (base, base.parent):
        if parent.exists() and exporter.is_link(parent):
            raise RuntimeError('Refusing linked test output')
    if base.exists():
        shutil.rmtree(base)


class ExportTests(unittest.TestCase):
    def setUp(self):
        base = REPO / 'artifacts' / 'code-export-tests'
        base.mkdir(parents=True, exist_ok=True)
        self.work = Path(tempfile.mkdtemp(prefix='case_', dir=base))
        self.root = self.work / 'repo'
        self.root.mkdir()
        self.out = self.work / 'exports'
        for path in exporter.START_HERE:
            self.put(path, 'Governance for ' + path + '\n')

    def put(self, path, content):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content if isinstance(content, bytes) else content.encode('utf-8'))
        return target

    def run_export(self, **kwargs):
        with contextlib.redirect_stdout(io.StringIO()):
            return exporter.run_aggregator(self.root, self.out, **kwargs)

    def text(self, folder):
        return '\n'.join(p.read_text(encoding='utf-8') for p in sorted(folder.glob('*.txt')))

    def test_installer_and_all_test_file_types_are_included(self):
        paths = ['build/installer/FreeVideoStudio.iss',
                 'tests/Core/AudioTempoFilterBuilderTests.cs', 'tests/Core/SpeedBuilderTests.cs',
                 'tests/Core/Test.csproj', 'tests/Smoke/README.md', 'tests/Smoke/Program.cs']
        for path in paths:
            self.put(path, 'source for ' + path)
        entries = {e.path: e for e in exporter.collect_entries(self.root)}
        for path in paths:
            self.assertEqual('TEXT', entries[path].status)
            if path.startswith('tests/'):
                self.assertEqual('06_Tests', entries[path].group)

    def test_cap_preserves_every_whole_file_even_when_huge(self):
        items = [(str(i), 'x' * (900_000 if i == 4 else 100_000)) for i in range(45)]
        parts = exporter.pack_into_capped_parts(items)
        self.assertLessEqual(len(parts), 8)
        self.assertEqual(items, [item for part in parts for item in part])
        self.assertEqual([], exporter.pack_into_capped_parts([]))
        with self.assertRaises(ValueError):
            exporter.pack_into_capped_parts(items, 0)

    def test_new_folders_preserve_previous_files_and_caches(self):
        self.out.mkdir()
        previous = self.out / 'personal_notes.txt'
        previous.write_text('KEEP', encoding='utf-8')
        cached = self.put('developer_tools/__pycache__/keep.pyc', b'KEEP')
        a, _ = self.run_export()
        before = {p.name: p.read_bytes() for p in a.glob('*.txt')}
        b, _ = self.run_export()
        self.assertNotEqual(a, b)
        self.assertEqual(before, {p.name: p.read_bytes() for p in a.glob('*.txt')})
        self.assertEqual('KEEP', previous.read_text())
        self.assertEqual(b'KEEP', cached.read_bytes())
        self.assertLessEqual(len(list(b.iterdir())), 10)

    def test_live_manifest_and_exact_file_index(self):
        self.put('meme/a.png', b'asset')
        self.put('src/FreeVideoStudio.App/Window.cs', 'first\nsecond\n')
        entries = exporter.collect_entries(self.root)
        payloads = exporter.make_payloads(entries)
        for e in entries:
            if e.status == 'TEXT':
                self.assertEqual('FILE: ' + e.path, payloads[e.bundle].splitlines()[e.line - 1])
                self.assertEqual(hashlib.sha256(e.text.encode('utf-8')).hexdigest(), e.digest)
        folder, status = self.run_export()
        manifest = (folder / '00_PROJECT_MANIFEST.txt').read_text(encoding='utf-8')
        self.assertEqual(0, status)
        self.assertIn('meme', manifest)
        self.assertNotIn('mp4\\', manifest)
        self.assertNotIn('bundle 07', manifest)
        self.assertIn('none requested; no tests were run', manifest)

    def test_unknown_files_assets_and_excluded_subtrees_are_accounted(self):
        self.put('meme/a.mp4', b'\0\1')
        self.put('custom.data', b'unknown')
        self.put('src/obj/generated.cs', 'generated')
        entries = {e.path: e for e in exporter.collect_entries(self.root)}
        self.assertEqual('METADATA', entries['custom.data'].status)
        self.assertEqual('METADATA', entries['meme/a.mp4'].status)
        self.assertIn('entire subtree skipped', entries['src/obj/'].reason)
        self.assertNotIn('src/obj/generated.cs', entries)

    def test_all_supported_unicode_encodings_roundtrip(self):
        content = 'Hello שלום\n'
        for encoding in ['utf-8', 'utf-8-sig', 'utf-16', 'utf-32', 'utf-16-le', 'utf-16-be']:
            with self.subTest(encoding=encoding):
                data = content.encode(encoding)
                self.assertEqual(content, exporter.decode_text(data)[0])

    def test_bad_encoding_is_error_not_replacement_or_success(self):
        self.put('bad.cs', b'bad\xff\xfe')
        folder, status = self.run_export()
        self.assertEqual(2, status)
        all_text = self.text(folder)
        self.assertIn('bad.cs | ERROR', all_text)
        self.assertIn('INCOMPLETE', all_text)
        self.assertNotIn('\ufffd', all_text)

    def test_legacy_encoding_is_explicit_and_labelled(self):
        self.put('attribution.txt', 'Copyright \u00a9 Artist'.encode('cp1252'))
        folder, status = self.run_export(legacy_encoding='cp1252')
        self.assertEqual(0, status)
        self.assertIn('Copyright \u00a9 Artist', self.text(folder))
        self.assertIn('cp1252 (user-selected fallback', self.text(folder))

    def test_known_repository_attribution_needs_no_command_line_flags(self):
        path = next(iter(exporter.LEGACY_ENCODINGS))
        self.put(path, 'Artist \u2014 song'.encode('cp1252'))
        folder, status = self.run_export()
        self.assertEqual(0, status)
        self.assertIn('Artist \u2014 song', self.text(folder))
        self.assertIn('repository-declared fallback', self.text(folder))

    def test_unreadable_file_fails_without_exception_content(self):
        path = self.put('file.cs', 'data')
        with patch.object(Path, 'read_bytes', side_effect=PermissionError('PRIVATE_VALUE')):
            entry = exporter.read_entry(path, 'file.cs', '01_Core_Logic')
        self.assertEqual('ERROR', entry.status)
        self.assertNotIn('PRIVATE_VALUE', entry.reason)

    def test_credentials_in_text_are_redacted_in_every_output(self):
        token = 'sk-' + 'A1' * 24
        password = 'fixture-' + 'not-a-real-password'
        private = '-----BEGIN ' + 'PRIVATE KEY-----\nfixture material\n-----END PRIVATE KEY-----'
        self.put('settings.json', '{"ApiKey": "' + token + '", "password": "' + password + '"}')
        self.put('ordinary.txt', private)
        self.put('config.ini', 'API_KEY=' + token + '\nPASSWORD=' + password)
        folder, _ = self.run_export()
        text = self.text(folder)
        for value in [token, password, 'fixture material']:
            self.assertNotIn(value, text)
        self.assertIn('REDACTED', text)

    def test_sensitive_files_are_never_read(self):
        for path in ['.env', 'sign.key.pem', 'sign.password.txt', 'credentials.json']:
            f = self.put(path, 'SECRET')
            with patch.object(Path, 'read_bytes', side_effect=AssertionError('must not read')):
                self.assertEqual('SKIPPED', exporter.read_entry(f, path, '').status)

    def test_redaction_preserves_safe_code_and_empty_fields(self):
        text = 'var value = settings.ApiKey;\n{"ApiKey": "", "password": "placeholder"}\n<TextBox PasswordChar="*" />'
        self.assertEqual(text, exporter.redact_sensitive(text)[0])

    def test_focus_keeps_governance_configs_and_all_tests(self):
        self.put('src/FreeVideoStudio.App/A.cs', 'A')
        self.put('src/FreeVideoStudio.App/B.cs', 'B')
        self.put('tests/Core/BTests.cs', 'tests')
        self.put('build/FvsBuild/project.csproj', 'project')
        entries = {e.path: e for e in exporter.collect_entries(self.root, ['src/**/A.cs'])}
        for p in exporter.START_HERE + ['src/FreeVideoStudio.App/A.cs', 'tests/Core/BTests.cs', 'build/FvsBuild/project.csproj']:
            self.assertEqual('TEXT', entries[p].status)
        self.assertEqual('SKIPPED', entries['src/FreeVideoStudio.App/B.cs'].status)

    def test_gui_budget_unknown_by_default_and_never_claims_fit(self):
        report = exporter.context_report({'part.txt': 'x' * 800})
        self.assertIn('GUI fit: UNKNOWN', report)
        self.assertIn('200 tokens', report)
        report = exporter.context_report({'part.txt': 'x' * 800}, 210, 20, 500)
        self.assertIn('estimated attachment tokens exceed', report)
        self.assertIn('per-file byte limit', report)
        self.assertIn('NOT guaranteed', report)

    def test_upload_report_counts_final_written_bytes(self):
        self.put('app.cs', 'hello שלום\n')
        folder, _ = self.run_export()
        total = sum(p.stat().st_size for p in folder.iterdir())
        manifest = (folder / '00_PROJECT_MANIFEST.txt').read_text(encoding='utf-8')
        self.assertIn(f'Attachment text: {total:,} UTF-8 bytes', manifest)
        self.assertIn(f'roughly {math.ceil(total / 4):,} tokens', manifest)

    def test_report_opt_in_filters_credentials_and_disclaims_freshness(self):
        report = self.put('TestResults/sample.trx', '<Result outcome="Passed" />')
        folder, _ = self.run_export(test_results=[report])
        text = self.text(folder)
        self.assertIn('[test-results]/1_sample.trx', text)
        self.assertIn('may be stale/unrelated', text)
        self.assertIn('outcome="Passed"', text)

    def test_output_inside_repository_is_refused(self):
        with self.assertRaises(ValueError):
            exporter.run_aggregator(self.root, self.root / 'exports')

    def test_git_snapshot_and_tracked_missing_files(self):
        state = {'commit': 'a' * 40, 'status': ' M app.cs', 'available': True,
                 'tracked': ['gone.cs', 'obj/generated.cs']}
        self.put('obj/generated.cs', 'generated')
        with patch.object(exporter, 'git_snapshot', return_value=state):
            folder, status = self.run_export()
        self.assertEqual(2, status)
        text = self.text(folder)
        self.assertIn('a' * 40, text)
        self.assertIn('gone.cs | ERROR', text)
        self.assertIn('obj/generated.cs | SKIPPED', text)

    def test_link_target_is_not_read(self):
        f = self.put('linked.cs', 'content')
        with patch.object(exporter, 'is_link', return_value=True), patch.object(Path, 'read_bytes', side_effect=AssertionError):
            self.assertEqual('SKIPPED', exporter.read_entry(f, 'linked.cs', '').status)

    def test_repository_selection_covers_current_installer_and_tests(self):
        entries = {e.path: e for e in exporter.collect_entries(REPO)}
        self.assertIn(entries['build/installer/FreeVideoStudio.iss'].status, {'TEXT', 'REDACTED'})
        for path in (REPO / 'tests').rglob('*'):
            rel = path.relative_to(REPO)
            if path.is_file() and not any(p.lower() in exporter.IGNORED_DIRS for p in rel.parts):
                self.assertIn(rel.as_posix(), entries)
                eligible = path.suffix.lower() in exporter.SOURCE_EXTENSIONS or path.name.lower() in exporter.SOURCE_NAMES
                expected = {'TEXT', 'REDACTED'} if eligible else {'METADATA'}
                self.assertIn(entries[rel.as_posix()].status, expected, rel.as_posix())
                self.assertEqual('06_Tests', entries[rel.as_posix()].group)

    def test_generated_av_outputs_are_excluded_but_test_sources_and_asset_metadata_remain(self):
        self.put('tests/AvRegression/media/generated.mp4', b'generated movie')
        self.put('tests/AvRegression/results/old.trx', '<old/>')
        self.put('tests/FreeVideoStudio.AvRegression.Tests/Scenarios/Playback.cs', 'source')
        self.put('tests/fixtures/reference.mp4', b'reference movie')
        entries = {e.path: e for e in exporter.collect_entries(self.root)}
        self.assertEqual('SKIPPED', entries['tests/AvRegression/'].status)
        self.assertNotIn('tests/AvRegression/media/generated.mp4', entries)
        self.assertEqual('TEXT', entries['tests/FreeVideoStudio.AvRegression.Tests/Scenarios/Playback.cs'].status)
        self.assertEqual('METADATA', entries['tests/fixtures/reference.mp4'].status)


if __name__ == '__main__':
    unittest.main()
